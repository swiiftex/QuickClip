#include "Audio.h"

#include "Engine.h"

#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <avrt.h>
#include <mmdeviceapi.h>
#include <wrl/client.h>
#include <wrl/implements.h>
#include <algorithm>
#include <cmath>

extern "C" {
#include <libavutil/mem.h>
#include <libavutil/tx.h>
#include <libswresample/swresample.h>
}

using Microsoft::WRL::ClassicCom;
using Microsoft::WRL::ComPtr;
using Microsoft::WRL::FtmBase;
using Microsoft::WRL::Make;
using Microsoft::WRL::RuntimeClass;
using Microsoft::WRL::RuntimeClassFlags;

namespace qc
{
    // ---------------------------------------------------------------------------------------------
    // TrackMixer
    // ---------------------------------------------------------------------------------------------

    TrackMixer::TrackMixer(int64_t t0Us) : ring_(static_cast<size_t>(Capacity * Channels)), t0Us_(t0Us) {}

    void TrackMixer::Add(Cursor& cursor, const float* data, uint32_t frames, int64_t timeUs)
    {
        int64_t pos = (timeUs - t0Us_) * SampleRate / 1000000;
        // Keep a stream contiguous unless its clock has really moved (40 ms): small timestamp jitter
        // would otherwise turn into clicks.
        if (cursor.next >= 0 && std::llabs(pos - cursor.next) < SampleRate / 25)
            pos = cursor.next;
        cursor.next = pos + frames;
        if (!data) return;

        std::lock_guard guard(lock_);
        for (uint32_t i = 0; i < frames; i++)
        {
            int64_t index = pos + i;
            if (index < base_) continue;                // arrived after that block was encoded
            if (index >= base_ + Capacity) break;       // impossibly far ahead
            size_t slot = static_cast<size_t>(index % Capacity) * Channels;
            ring_[slot] += data[i * 2];
            ring_[slot + 1] += data[i * 2 + 1];
        }
    }

    void TrackMixer::Take(int64_t upTo, std::vector<float>& out)
    {
        out.clear();
        std::lock_guard guard(lock_);
        if (upTo <= base_) return;
        int64_t count = upTo - base_;
        out.resize(static_cast<size_t>(count * Channels));
        for (int64_t i = 0; i < count && i < Capacity; i++)
        {
            size_t slot = static_cast<size_t>((base_ + i) % Capacity) * Channels;
            out[static_cast<size_t>(i * Channels)] = ring_[slot];
            out[static_cast<size_t>(i * Channels + 1)] = ring_[slot + 1];
            ring_[slot] = ring_[slot + 1] = 0;
        }
        base_ = upTo;
    }

    // ---------------------------------------------------------------------------------------------
    // MixerSink
    // ---------------------------------------------------------------------------------------------

    MixerSink::MixerSink(TrackMixer& mixer, int rate) : mixer_(mixer), rate_(rate)
    {
        if (rate_ == SampleRate) return;
        AVChannelLayout stereo = AV_CHANNEL_LAYOUT_STEREO;
        if (swr_alloc_set_opts2(&swr_, &stereo, AV_SAMPLE_FMT_FLT, SampleRate, &stereo, AV_SAMPLE_FMT_FLT, rate_, 0, nullptr) < 0
            || swr_init(swr_) < 0)
        {
            Log("converting %d Hz audio to %d Hz isn't available; that audio is left out", rate_, SampleRate);
            swr_free(&swr_);
        }
    }

    MixerSink::~MixerSink() { swr_free(&swr_); }

    void MixerSink::Push(const float* data, uint32_t frames, int64_t timeUs, bool restart)
    {
        if (restart) cursor_ = {};
        if (rate_ == SampleRate)
        {
            mixer_.Add(cursor_, data, frames, timeUs);
            return;
        }
        if (!swr_) return;
        if (restart)
        {
            swr_close(swr_);
            swr_init(swr_);
        }
        // Silence goes through too, so the converter's timing carries on unbroken.
        if (!data)
        {
            silence_.assign(static_cast<size_t>(frames) * Channels, 0.f);
            data = silence_.data();
        }
        int capacity = swr_get_out_samples(swr_, static_cast<int>(frames));
        if (capacity <= 0) return;
        converted_.resize(static_cast<size_t>(capacity) * Channels);
        auto* out = reinterpret_cast<uint8_t*>(converted_.data());
        auto* in = reinterpret_cast<const uint8_t*>(data);
        int n = swr_convert(swr_, &out, capacity, &in, static_cast<int>(frames));
        if (n > 0) mixer_.Add(cursor_, converted_.data(), static_cast<uint32_t>(n), timeUs);
    }

    // ---------------------------------------------------------------------------------------------
    // Canceller
    // ---------------------------------------------------------------------------------------------

    namespace
    {
        constexpr int64_t HoldUs = 120'000;       // the newest audio waits this long for the apps' captures to catch up
        constexpr int64_t StallUs = 300'000;      // an app's capture this far behind is taken as silent
        constexpr int64_t SlackUs = 20'000;       // timestamps that differ from frame counting by less are the same thing
        constexpr int64_t MaxGapUs = 1'000'000;   // a longer break than this starts a capture over
        constexpr int KeepSeconds = 3;            // history kept for finding offsets

        /// c[l] = sum over k of a[k] * b[k + l], for l in [0, lags), by FFT. Empty if that isn't available.
        std::vector<double> CrossCorrelate(const std::vector<float>& a, const std::vector<float>& b, size_t lags)
        {
            int n = 1;
            while (static_cast<size_t>(n) < a.size() + b.size()) n <<= 1;
            AVTXContext *forward = nullptr, *inverse = nullptr;
            av_tx_fn forwardFn = nullptr, inverseFn = nullptr;
            float one = 1.0f, scale = 1.0f / static_cast<float>(n);
            std::vector<double> result;
            auto* ra = static_cast<float*>(av_mallocz(sizeof(float) * (n + 2)));
            auto* rb = static_cast<float*>(av_mallocz(sizeof(float) * (n + 2)));
            auto* fa = static_cast<AVComplexFloat*>(av_mallocz(sizeof(AVComplexFloat) * (n / 2 + 1)));
            auto* fb = static_cast<AVComplexFloat*>(av_mallocz(sizeof(AVComplexFloat) * (n / 2 + 1)));
            if (ra && rb && fa && fb && av_tx_init(&forward, &forwardFn, AV_TX_FLOAT_RDFT, 0, n, &one, 0) >= 0
                && av_tx_init(&inverse, &inverseFn, AV_TX_FLOAT_RDFT, 1, n, &scale, 0) >= 0)
            {
                std::copy(a.begin(), a.end(), ra);
                std::copy(b.begin(), b.end(), rb);
                forwardFn(forward, fa, ra, sizeof(float));
                forwardFn(forward, fb, rb, sizeof(float));
                for (int i = 0; i <= n / 2; i++)
                {
                    // conj(A) * B: b lagging a shows up at positive lags
                    float re = fa[i].re * fb[i].re + fa[i].im * fb[i].im;
                    float im = fa[i].re * fb[i].im - fa[i].im * fb[i].re;
                    fb[i] = { re, im };
                }
                inverseFn(inverse, ra, fb, sizeof(AVComplexFloat));
                result.assign(ra, ra + std::min<size_t>(lags, static_cast<size_t>(n)));
            }
            av_tx_uninit(&forward);
            av_tx_uninit(&inverse);
            av_free(ra);
            av_free(rb);
            av_free(fa);
            av_free(fb);
            return result;
        }

        /// Changes from one frame to the next, which bring out what's distinctive in a sound.
        std::vector<float> Changes(const std::vector<float>& m)
        {
            std::vector<float> d(m.size() > 0 ? m.size() - 1 : 0);
            for (size_t i = 0; i < d.size(); i++) d[i] = m[i + 1] - m[i];
            return d;
        }
    }

    Canceller::Canceller(int rate, std::unique_ptr<AudioSink> out) : rate_(rate), out_(std::move(out)), everythingInput_(*this, 0) {}

    AudioSink& Canceller::Reference(DWORD pid)
    {
        std::lock_guard guard(lock_);
        auto& input = inputs_[pid];
        if (!input)
        {
            input = std::make_unique<Input>(*this, pid);
            auto ref = std::make_unique<Ref>();
            ref->pid = pid;
            refs_[pid] = std::move(ref);
        }
        return *input;
    }

    void Canceller::RemoveReference(DWORD pid)
    {
        std::lock_guard guard(lock_);
        refs_.erase(pid);
        inputs_.erase(pid);
    }

    int64_t Canceller::OffsetOf(DWORD pid)
    {
        std::lock_guard guard(lock_);
        auto it = refs_.find(pid);
        return it != refs_.end() && it->second->locked ? it->second->offset : INT64_MIN;
    }

    int64_t Canceller::TimeToIndex(const Stream& s, int64_t timeUs) const
    {
        return s.anchorIndex + (timeUs - s.anchorUs) * rate_ / 1000000;
    }

    int64_t Canceller::TimeOf(const Stream& s, int64_t index) const
    {
        return s.anchorUs + (index - s.anchorIndex) * 1000000 / rate_;
    }

    std::vector<float> Canceller::Mono(const Stream& s, int64_t from, int64_t to) const
    {
        std::vector<float> m(static_cast<size_t>(std::max<int64_t>(0, to - from)));
        for (int64_t i = from; i < to; i++)
        {
            size_t k = static_cast<size_t>(i - s.base) * 2;
            m[static_cast<size_t>(i - from)] = s.samples[k] + s.samples[k + 1];
        }
        return m;
    }

    void Canceller::Append(DWORD pid, const float* data, uint32_t frames, int64_t timeUs, bool restart)
    {
        std::lock_guard guard(lock_);
        Stream* s = &everything_;
        Ref* ref = nullptr;
        if (pid != 0)
        {
            auto it = refs_.find(pid);
            if (it == refs_.end()) return;
            ref = it->second.get();
            s = &ref->stream;
        }

        bool lost = false;
        if (s->anchorIndex >= 0 && !restart)
        {
            int64_t driftUs = timeUs - TimeOf(*s, s->End());
            if (std::llabs(driftUs) <= SlackUs)
            {
                // Follows on. The audio clock and the timestamps' clock drift apart slowly; keep to the latter.
                s->anchorIndex = s->End();
                s->anchorUs = timeUs;
            }
            else if (driftUs > 0 && driftUs < MaxGapUs)
            {
                // A stretch was lost: silence in its place keeps frame numbers in step with time.
                s->samples.insert(s->samples.end(), static_cast<size_t>(driftUs * rate_ / 1000000) * Channels, 0.f);
                s->anchorIndex = s->End();
                s->anchorUs = timeUs;
                lost = true;
            }
            else restart = true;
        }
        if (restart || s->anchorIndex < 0)
        {
            // A new capture stream: its frames don't line up with anything from before.
            int64_t next = s->End();
            s->samples.clear();
            s->base = s->anchorIndex = next;
            s->anchorUs = timeUs;
            if (ref) ref->locked = false;
            else
            {
                for (auto& [id, r] : refs_) r->locked = false;
                emitted_ = s->base;
                restartOut_ = true;
            }
        }
        else if (lost)
        {
            // The offsets should still hold; check them soon.
            if (ref) ref->nextCheckUs = 0;
            else for (auto& [id, r] : refs_) r->nextCheckUs = 0;
        }

        if (data) s->samples.insert(s->samples.end(), data, data + static_cast<size_t>(frames) * Channels);
        else s->samples.insert(s->samples.end(), static_cast<size_t>(frames) * Channels, 0.f);
        s->lastPushUs = NowUs();
    }

    double Canceller::Match(const Stream& app, int64_t j0, int64_t j1, int64_t offset) const
    {
        if (j0 - 1 < app.base || j1 > app.End() || j0 - 1 + offset < everything_.base || j1 + offset > everything_.End()) return 0;
        auto r = Mono(app, j0 - 1, j1);
        auto e = Mono(everything_, j0 - 1 + offset, j1 + offset);
        double both = 0, own = 0;
        for (size_t i = 1; i < r.size(); i++)
        {
            double dr = r[i] - r[i - 1];
            both += dr * (e[i] - e[i - 1]);
            own += dr * dr;
        }
        return own > 0 ? both / own : 0;
    }

    void Canceller::Search(Ref& ref, int64_t nowUs)
    {
        ref.nextSearchUs = nowUs + 500'000;
        const Stream& app = ref.stream;
        if (ref.sinceUs == 0) ref.sinceUs = nowUs;
        if (!ref.waitLogged && nowUs - ref.sinceUs > 5'000'000)
        {
            ref.waitLogged = true;
            Log("desktop: still looking for process %lu's sound after 5 s (%.1f s of it held, %.1f s of everything)", ref.pid,
                static_cast<double>(app.End() - app.base) / rate_, static_cast<double>(everything_.End() - everything_.base) / rate_);
        }
        if (app.anchorIndex < 0 || everything_.anchorIndex < 0) return;
        // The latest second of the app's sound that everything's capture has caught up with, give or take 100 ms around
        // where the timestamps put it.
        int64_t guess = TimeToIndex(everything_, TimeOf(app, app.End())) - app.End();
        int64_t window = rate_ / 10;
        int64_t j1 = std::min(app.End(), everything_.End() - guess - window), j0 = j1 - rate_;
        if (j0 - 1 < app.base || j0 - 1 + guess - window < everything_.base) return;
        double energy = 0;
        for (float v : Mono(app, j0, j1)) energy += static_cast<double>(v) * v;
        if (energy / static_cast<double>(j1 - j0) < 1e-12) return; // silent: nothing to find (or take out)

        // Compare changes from frame to frame across the whole ±100 ms: they pin down the exact frame, and stand out even
        // under much louder other sound.
        int64_t e0 = j0 + guess - window;
        auto correlation = CrossCorrelate(Changes(Mono(app, j0 - 1, j1)), Changes(Mono(everything_, e0 - 1, j1 + guess + window)),
            static_cast<size_t>(2 * window + 1));
        if (correlation.empty()) return;
        size_t peak = static_cast<size_t>(std::max_element(correlation.begin(), correlation.end()) - correlation.begin());
        int64_t best = e0 + static_cast<int64_t>(peak) - j0;
        double match = Match(app, j0, j1, best);
        if (match > 0.8 && match < 1.2)
        {
            ref.locked = true;
            ref.offset = best;
            ref.nextCheckUs = nowUs + 2'000'000;
            ref.failureLogged = ref.waitLogged = false;
            ref.sinceUs = 0;
            Log("desktop: found process %lu's sound in it (%.2f ms from the timestamps), taking it out", ref.pid,
                (best - guess) * 1000.0 / rate_);
        }
        else if (!ref.failureLogged)
        {
            Log("desktop: process %lu's sound isn't matched in it yet (%.2f)", ref.pid, match);
            ref.failureLogged = true;
        }
    }

    void Canceller::Check(Ref& ref, int64_t nowUs)
    {
        ref.nextCheckUs = nowUs + 2'000'000;
        const Stream& app = ref.stream;
        int64_t j1 = std::min(app.End(), everything_.End() - ref.offset), j0 = j1 - rate_ / 2;
        if (j0 - 1 < app.base || j0 - 1 + ref.offset < everything_.base) return;
        double energy = 0;
        for (float v : Mono(app, j0, j1)) energy += static_cast<double>(v) * v;
        if (energy / static_cast<double>(j1 - j0) < 1e-12) return; // silent: nothing to tell by
        double match = Match(app, j0, j1, ref.offset);
        if (match > 0.7 && match < 1.3) return;
        // Slipped a little? Look close by before starting over.
        for (int64_t d = ref.offset - 64; d <= ref.offset + 64; d++)
        {
            double m = Match(app, j0, j1, d);
            if (m > 0.8 && m < 1.2)
            {
                Log("desktop: process %lu's sound moved %lld frames; following it", ref.pid, static_cast<long long>(d - ref.offset));
                ref.offset = d;
                return;
            }
        }
        Log("desktop: lost process %lu's sound in it (%.2f); finding it again", ref.pid, match);
        ref.locked = false;
        ref.nextSearchUs = nowUs;
    }

    void Canceller::Process()
    {
        std::lock_guard guard(lock_);
        int64_t now = NowUs();
        for (auto& [pid, ref] : refs_)
        {
            if (!ref->locked)
            {
                if (now >= ref->nextSearchUs) Search(*ref, now);
            }
            else if (now >= ref->nextCheckUs) Check(*ref, now);
        }

        // Pass on everything captured up to a moment ago, less the apps found in it, once their captures have
        // caught up (one that has stalled is taken as silent).
        if (everything_.anchorIndex >= 0)
        {
            if (emitted_ < everything_.base) emitted_ = everything_.base;
            int64_t limit = std::min(everything_.End(), TimeToIndex(everything_, now - HoldUs));
            for (auto& [pid, ref] : refs_)
                if (ref->locked && now - ref->stream.lastPushUs < StallUs)
                    limit = std::min(limit, ref->stream.End() + ref->offset);
            if (limit > emitted_)
            {
                size_t count = static_cast<size_t>(limit - emitted_);
                outBuffer_.resize(count * Channels);
                for (size_t i = 0; i < count; i++)
                {
                    int64_t n = emitted_ + static_cast<int64_t>(i);
                    size_t k = static_cast<size_t>(n - everything_.base) * 2;
                    float left = everything_.samples[k], right = everything_.samples[k + 1];
                    for (auto& [pid, ref] : refs_)
                    {
                        if (!ref->locked) continue;
                        int64_t j = n - ref->offset;
                        if (j < ref->stream.base || j >= ref->stream.End()) continue;
                        size_t a = static_cast<size_t>(j - ref->stream.base) * 2;
                        left -= ref->stream.samples[a];
                        right -= ref->stream.samples[a + 1];
                    }
                    outBuffer_[i * 2] = left;
                    outBuffer_[i * 2 + 1] = right;
                }
                out_->Push(outBuffer_.data(), static_cast<uint32_t>(count), TimeOf(everything_, emitted_), restartOut_);
                restartOut_ = false;
                emitted_ = limit;
            }
        }

        // Keep a few seconds for finding offsets.
        auto trim = [&](Stream& s, int64_t keepFrom) {
            if (keepFrom <= s.base) return;
            int64_t drop = std::min(keepFrom, s.End()) - s.base;
            s.samples.erase(s.samples.begin(), s.samples.begin() + static_cast<std::ptrdiff_t>(drop * 2));
            s.base += drop;
        };
        int64_t keep = static_cast<int64_t>(rate_) * KeepSeconds;
        trim(everything_, std::min(emitted_, everything_.End() - keep));
        for (auto& [pid, ref] : refs_) trim(ref->stream, ref->stream.End() - keep);
    }

    // ---------------------------------------------------------------------------------------------
    // AudioSource
    // ---------------------------------------------------------------------------------------------

    namespace
    {
        class ActivationHandler
            : public RuntimeClass<RuntimeClassFlags<ClassicCom>, FtmBase, IActivateAudioInterfaceCompletionHandler>
        {
        public:
            ActivationHandler() : done(CreateEventW(nullptr, TRUE, FALSE, nullptr)) {}
            ~ActivationHandler() override { CloseHandle(done); }

            STDMETHOD(ActivateCompleted)(IActivateAudioInterfaceAsyncOperation* operation) override
            {
                HRESULT activateResult = E_FAIL;
                ComPtr<IUnknown> unknown;
                HRESULT hr = operation->GetActivateResult(&activateResult, &unknown);
                result = FAILED(hr) ? hr : activateResult;
                if (SUCCEEDED(result)) result = unknown.As(&client);
                SetEvent(done);
                return S_OK;
            }

            HANDLE done;
            HRESULT result = E_FAIL;
            ComPtr<IAudioClient> client;
        };

        HRESULT ActivateProcessLoopback(DWORD pid, bool include, ComPtr<IAudioClient>& client)
        {
            AUDIOCLIENT_ACTIVATION_PARAMS params{};
            params.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
            params.ProcessLoopbackParams.TargetProcessId = pid;
            params.ProcessLoopbackParams.ProcessLoopbackMode = include
                ? PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
                : PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE;
            PROPVARIANT pv{};
            pv.vt = VT_BLOB;
            pv.blob.cbSize = sizeof(params);
            pv.blob.pBlobData = reinterpret_cast<BYTE*>(&params);

            auto handler = Make<ActivationHandler>();
            ComPtr<IActivateAudioInterfaceAsyncOperation> operation;
            HRESULT hr = ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, __uuidof(IAudioClient), &pv,
                handler.Get(), &operation);
            if (FAILED(hr)) return hr;
            if (WaitForSingleObject(handler->done, 5000) != WAIT_OBJECT_0) return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
            if (FAILED(handler->result)) return handler->result;
            client = handler->client;
            return S_OK;
        }

        HRESULT ActivateMicrophone(const std::wstring& deviceId, ComPtr<IAudioClient>& client)
        {
            ComPtr<IMMDeviceEnumerator> enumerator;
            HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator));
            if (FAILED(hr)) return hr;
            ComPtr<IMMDevice> device;
            hr = deviceId.empty()
                ? enumerator->GetDefaultAudioEndpoint(eCapture, eConsole, &device)
                : enumerator->GetDevice(deviceId.c_str(), &device);
            if (FAILED(hr)) return hr;
            return device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, &client);
        }

        HRESULT ActivateDefaultOutput(ComPtr<IAudioClient>& client)
        {
            ComPtr<IMMDeviceEnumerator> enumerator;
            HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator));
            if (FAILED(hr)) return hr;
            ComPtr<IMMDevice> device;
            hr = enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device);
            if (FAILED(hr)) return hr;
            return device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, &client);
        }

        // Lets tests exercise the older-Windows path on a machine that has per-app capture.
        bool ProcessLoopbackDisabled()
        {
            wchar_t value[8];
            return GetEnvironmentVariableW(L"QUICKCLIP_NO_PROCESS_LOOPBACK", value, 8) > 0;
        }
    }

    bool PerAppCaptureAvailable()
    {
        static const bool supported = [] {
            // GetVersionEx reports what the host's manifest claims to support; ntdll reports the real build.
            using RtlGetVersionFn = LONG(WINAPI*)(OSVERSIONINFOW*);
            auto getVersion = reinterpret_cast<RtlGetVersionFn>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlGetVersion"));
            OSVERSIONINFOW version{ sizeof(version) };
            return getVersion && getVersion(&version) == 0 && version.dwBuildNumber >= 20348;
        }();
        return supported && !ProcessLoopbackDisabled();
    }

    int DefaultOutputRate()
    {
        HRESULT init = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        int rate = SampleRate;
        ComPtr<IAudioClient> client;
        WAVEFORMATEX* mix = nullptr;
        if (SUCCEEDED(ActivateDefaultOutput(client)) && SUCCEEDED(client->GetMixFormat(&mix)) && mix)
        {
            if (mix->nSamplesPerSec >= 8000 && mix->nSamplesPerSec <= 384000) rate = static_cast<int>(mix->nSamplesPerSec);
            CoTaskMemFree(mix);
        }
        if (SUCCEEDED(init)) CoUninitialize();
        return rate;
    }

    AudioSource::AudioSource(SourceKind kind, DWORD pid, std::wstring deviceId, int rate, std::unique_ptr<AudioSink> sink)
        : kind_(kind), pid_(pid), deviceId_(std::move(deviceId)), rate_(rate), sink_(std::move(sink)),
          stop_(CreateEventW(nullptr, TRUE, FALSE, nullptr))
    {
        thread_ = std::thread([this] { Run(); });
    }

    AudioSource::~AudioSource()
    {
        SetEvent(stop_);
        if (thread_.joinable()) thread_.join();
        CloseHandle(stop_);
    }

    void AudioSource::Run()
    {
        CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        DWORD taskIndex = 0;
        HANDLE task = AvSetMmThreadCharacteristicsW(L"Audio", &taskIndex);
        // Keep capturing until stopped; if the device disappears (or isn't there yet), try again, backing off
        // while it keeps failing.
        DWORD retryMs = 2000;
        HRESULT lastError = S_OK;
        while (WaitForSingleObject(stop_, 0) != WAIT_OBJECT_0)
        {
            HRESULT hr = CaptureOnce();
            if (FAILED(hr) && hr != lastError && kind_ == SourceKind::Microphone)
                Log("microphone %s stopped: 0x%08lx", deviceId_.empty() ? "(default)" : Narrow(deviceId_).c_str(), static_cast<unsigned long>(hr));
            else if (FAILED(hr) && hr != lastError)
                Log("audio source (kind %d, pid %lu) stopped: 0x%08lx", static_cast<int>(kind_), pid_, static_cast<unsigned long>(hr));
            lastError = hr;
            retryMs = SUCCEEDED(hr) ? 2000 : std::min<DWORD>(retryMs * 2, 30000);
            if (WaitForSingleObject(stop_, retryMs) == WAIT_OBJECT_0) break;
        }
        if (task) AvRevertMmThreadCharacteristics(task);
        CoUninitialize();
    }

    HRESULT AudioSource::CaptureOnce()
    {
        ComPtr<IAudioClient> client;
        HRESULT hr;
        if (kind_ == SourceKind::Microphone)
            hr = ActivateMicrophone(deviceId_, client);
        else
        {
            hr = PerAppCaptureAvailable() ? ActivateProcessLoopback(pid_, kind_ == SourceKind::ProcessInclude, client) : E_NOTIMPL;
            // Without per-app capture, Desktop records everything the speakers play.
            if (FAILED(hr) && kind_ == SourceKind::ProcessExclude)
            {
                if (!endpointFallbackLogged_)
                {
                    Log("per-app audio capture unavailable (0x%08lx); recording all system audio", static_cast<unsigned long>(hr));
                    endpointFallbackLogged_ = true;
                }
                hr = ActivateDefaultOutput(client);
            }
        }
        if (FAILED(hr)) return hr;

        WAVEFORMATEX format{};
        format.wFormatTag = WAVE_FORMAT_IEEE_FLOAT;
        format.nChannels = Channels;
        format.nSamplesPerSec = static_cast<DWORD>(rate_);
        format.wBitsPerSample = 32;
        format.nBlockAlign = Channels * 4;
        format.nAvgBytesPerSec = static_cast<DWORD>(rate_) * format.nBlockAlign;

        DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        if (kind_ != SourceKind::Microphone) flags |= AUDCLNT_STREAMFLAGS_LOOPBACK;
        hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 2000000 /* 200 ms */, 0, &format, nullptr);
        if (FAILED(hr)) return hr;

        ComPtr<IAudioCaptureClient> capture;
        hr = client->GetService(IID_PPV_ARGS(&capture));
        if (FAILED(hr)) return hr;
        HANDLE ready = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        client->SetEventHandle(ready);
        hr = client->Start();
        if (FAILED(hr))
        {
            CloseHandle(ready);
            return hr;
        }
        bool first = true; // a new stream, which doesn't follow on from the last

        HANDLE waits[] = { stop_, ready };
        while (true)
        {
            DWORD signaled = WaitForMultipleObjects(2, waits, FALSE, 500);
            if (signaled == WAIT_OBJECT_0) break;
            UINT32 packetFrames = 0;
            while (SUCCEEDED(hr = capture->GetNextPacketSize(&packetFrames)) && packetFrames > 0)
            {
                BYTE* data = nullptr;
                UINT32 frames = 0;
                DWORD bufferFlags = 0;
                UINT64 devicePosition = 0, qpcPosition = 0;
                hr = capture->GetBuffer(&data, &frames, &bufferFlags, &devicePosition, &qpcPosition);
                if (FAILED(hr)) break;
                // qpcPosition is the performance counter in 100 ns units: same clock as NowUs().
                int64_t timeUs = (qpcPosition == 0 || (bufferFlags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR))
                    ? NowUs() - static_cast<int64_t>(frames) * 1000000 / rate_
                    : static_cast<int64_t>(qpcPosition / 10);
                bool silent = (bufferFlags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                sink_->Push(silent ? nullptr : reinterpret_cast<const float*>(data), frames, timeUs, first);
                first = false;
                capture->ReleaseBuffer(frames);
            }
            if (FAILED(hr)) break;
        }
        client->Stop();
        CloseHandle(ready);
        return FAILED(hr) ? hr : S_OK;
    }

    void PlayTestNoise(int milliseconds, float amplitude)
    {
        std::thread([milliseconds, amplitude] {
            CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            ComPtr<IAudioClient> client;
            WAVEFORMATEX format{ WAVE_FORMAT_IEEE_FLOAT, Channels, SampleRate, SampleRate * Channels * 4, Channels * 4, 32, 0 };
            if (SUCCEEDED(ActivateDefaultOutput(client)) && SUCCEEDED(client->Initialize(AUDCLNT_SHAREMODE_SHARED,
                    AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                    1000000, 0, &format, nullptr)))
            {
                ComPtr<IAudioRenderClient> render;
                client->GetService(IID_PPV_ARGS(&render));
                UINT32 size = 0;
                client->GetBufferSize(&size);
                HANDLE ready = CreateEventW(nullptr, FALSE, FALSE, nullptr);
                client->SetEventHandle(ready);
                uint32_t seed = 12345;
                auto fill = [&](UINT32 frames) {
                    BYTE* data = nullptr;
                    if (FAILED(render->GetBuffer(frames, &data))) return;
                    auto* s = reinterpret_cast<float*>(data);
                    for (UINT32 i = 0; i < frames * Channels; i++)
                    {
                        seed = seed * 1664525 + 1013904223;
                        s[i] = (static_cast<float>(seed >> 8) / 8388608.0f - 1.0f) * amplitude;
                    }
                    render->ReleaseBuffer(frames, 0);
                };
                fill(size);
                client->Start();
                ULONGLONG end = GetTickCount64() + static_cast<ULONGLONG>(milliseconds);
                while (GetTickCount64() < end)
                {
                    WaitForSingleObject(ready, 100);
                    UINT32 padding = 0;
                    client->GetCurrentPadding(&padding);
                    if (size > padding) fill(size - padding);
                }
                client->Stop();
                CloseHandle(ready);
            }
            CoUninitialize();
        }).detach();
    }

    int EnumMicrophones(QcAudioDevice* devices, int max)
    {
        // PKEY_Device_FriendlyName
        static const PROPERTYKEY friendlyName = { { 0xa45c254e, 0xdf1c, 0x4efd, { 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0 } }, 14 };
        HRESULT init = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        int count = 0;
        ComPtr<IMMDeviceEnumerator> enumerator;
        ComPtr<IMMDeviceCollection> collection;
        if (SUCCEEDED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator)))
            && SUCCEEDED(enumerator->EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE, &collection)))
        {
            std::wstring defaultId;
            ComPtr<IMMDevice> def;
            LPWSTR id = nullptr;
            if (SUCCEEDED(enumerator->GetDefaultAudioEndpoint(eCapture, eConsole, &def)) && SUCCEEDED(def->GetId(&id)))
            {
                defaultId = id;
                CoTaskMemFree(id);
            }
            UINT n = 0;
            collection->GetCount(&n);
            for (UINT i = 0; i < n && count < max; i++)
            {
                ComPtr<IMMDevice> device;
                ComPtr<IPropertyStore> props;
                if (FAILED(collection->Item(i, &device)) || FAILED(device->GetId(&id))) continue;
                QcAudioDevice& d = devices[count++];
                wcsncpy_s(d.id, id, _TRUNCATE);
                d.isDefault = defaultId == id;
                CoTaskMemFree(id);
                d.name[0] = 0;
                PROPVARIANT name;
                PropVariantInit(&name);
                if (SUCCEEDED(device->OpenPropertyStore(STGM_READ, &props)) && SUCCEEDED(props->GetValue(friendlyName, &name)) && name.vt == VT_LPWSTR)
                    wcsncpy_s(d.name, name.pwszVal, _TRUNCATE);
                PropVariantClear(&name);
            }
        }
        if (SUCCEEDED(init)) CoUninitialize();
        return count;
    }

    // ---------------------------------------------------------------------------------------------
    // AudioEncoder
    // ---------------------------------------------------------------------------------------------

    AudioEncoder::~AudioEncoder()
    {
        for (AVPacket* p : produced_) av_packet_free(&p);
        av_packet_free(&packet_);
        av_frame_free(&frame_);
        avcodec_free_context(&ctx_);
    }

    bool AudioEncoder::Open(int bitrateKbps, std::string& error)
    {
        const AVCodec* codec = avcodec_find_encoder(AV_CODEC_ID_AAC);
        if (!codec) { error = "AAC encoder not available"; return false; }
        ctx_ = avcodec_alloc_context3(codec);
        ctx_->sample_fmt = AV_SAMPLE_FMT_FLTP;
        ctx_->sample_rate = SampleRate;
        av_channel_layout_default(&ctx_->ch_layout, Channels);
        ctx_->bit_rate = static_cast<int64_t>(bitrateKbps) * 1000;
        ctx_->time_base = AVRational{ 1, SampleRate };
        ctx_->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
        int ret = avcodec_open2(ctx_, codec, nullptr);
        if (ret < 0) { error = "Opening the AAC encoder failed: " + AvError(ret); return false; }

        frame_ = av_frame_alloc();
        frame_->format = ctx_->sample_fmt;
        frame_->nb_samples = ctx_->frame_size;
        frame_->sample_rate = SampleRate;
        av_channel_layout_copy(&frame_->ch_layout, &ctx_->ch_layout);
        av_frame_get_buffer(frame_, 0);
        packet_ = av_packet_alloc();
        return true;
    }

    void AudioEncoder::EncodeFrame(AVFrame* frame)
    {
        if (avcodec_send_frame(ctx_, frame) < 0) return;
        while (avcodec_receive_packet(ctx_, packet_) == 0)
        {
            produced_.push_back(av_packet_clone(packet_));
            av_packet_unref(packet_);
        }
    }
}
