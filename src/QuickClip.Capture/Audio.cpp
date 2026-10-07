#include "Audio.h"

#include "Engine.h"

#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <avrt.h>
#include <mmdeviceapi.h>
#include <wrl/client.h>
#include <wrl/implements.h>
#include <algorithm>

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

    AudioSource::AudioSource(SourceKind kind, DWORD pid, std::wstring deviceId, TrackMixer& mixer)
        : kind_(kind), pid_(pid), deviceId_(std::move(deviceId)), mixer_(mixer),
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
            if (FAILED(hr) && hr != lastError)
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
        format.nSamplesPerSec = SampleRate;
        format.wBitsPerSample = 32;
        format.nBlockAlign = Channels * 4;
        format.nAvgBytesPerSec = SampleRate * format.nBlockAlign;

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
        cursor_ = {};

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
                    ? NowUs() - static_cast<int64_t>(frames) * 1000000 / SampleRate
                    : static_cast<int64_t>(qpcPosition / 10);
                bool silent = (bufferFlags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                mixer_.Add(cursor_, silent ? nullptr : reinterpret_cast<const float*>(data), frames, timeUs);
                capture->ReleaseBuffer(frames);
            }
            if (FAILED(hr)) break;
        }
        client->Stop();
        CloseHandle(ready);
        return FAILED(hr) ? hr : S_OK;
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
