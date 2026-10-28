// Audio capture (WASAPI) and per-track mixing for the replay buffer.
#pragma once

#include "Common.h"
#include <atomic>
#include <deque>
#include <map>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>

struct SwrContext;

namespace qc
{
    /// Collects one output track (Desktop, Chat, Music or Mic) on the engine timeline. Sources add samples at the
    /// position their capture timestamp says; the encoder takes finished blocks in order. Gaps stay silent.
    class TrackMixer
    {
    public:
        explicit TrackMixer(int64_t t0Us);

        struct Cursor { int64_t next = -1; };

        /// Adds interleaved stereo float frames captured at timeUs (null data = silence).
        void Add(Cursor& cursor, const float* data, uint32_t frames, int64_t timeUs);

        /// Removes and returns all samples before sample index `upTo` (zero where nothing was captured).
        void Take(int64_t upTo, std::vector<float>& out);

        /// Sample index up to which audio has been handed to the encoder.
        int64_t Position() const { return base_; }

    private:
        static constexpr int64_t Capacity = SampleRate * 4; // frames
        std::mutex lock_;
        std::vector<float> ring_;
        std::atomic<int64_t> base_{ 0 };
        int64_t t0Us_;
    };

    /// Where a capture's audio goes: interleaved stereo float frames at the capture's rate (null data = silence)
    /// captured at timeUs. `restart`: the first frames of a new capture stream, which don't follow on from before.
    class AudioSink
    {
    public:
        virtual ~AudioSink() = default;
        virtual void Push(const float* data, uint32_t frames, int64_t timeUs, bool restart) = 0;
    };

    /// Places audio on a track, converted to the track's rate when it was captured at another.
    class MixerSink final : public AudioSink
    {
    public:
        MixerSink(TrackMixer& mixer, int rate);
        ~MixerSink() override;
        void Push(const float* data, uint32_t frames, int64_t timeUs, bool restart) override;

    private:
        TrackMixer& mixer_;
        int rate_;
        TrackMixer::Cursor cursor_;
        SwrContext* swr_ = nullptr;
        std::vector<float> silence_, converted_;
    };

    /// Hands audio to its own sink and to another one it doesn't own (Desktop's subtraction).
    class TeeSink final : public AudioSink
    {
    public:
        TeeSink(std::unique_ptr<AudioSink> own, AudioSink* other) : own_(std::move(own)), other_(other) {}
        void Push(const float* data, uint32_t frames, int64_t timeUs, bool restart) override
        {
            if (own_) own_->Push(data, frames, timeUs, restart);
            if (other_) other_->Push(data, frames, timeUs, restart);
        }

    private:
        std::unique_ptr<AudioSink> own_;
        AudioSink* other_;
    };

    /// Desktop as everything Windows plays (one capture) with other apps' audio taken out sample for sample, since
    /// Windows can leave only one app out of a capture. Every capture comes from the same audio engine at the
    /// output's own rate, so an app's samples are identical in each, just at a different offset that's found by
    /// matching the app's sound in both, and checked as it goes on. Until it's found, that app stays in.
    class Canceller
    {
    public:
        Canceller(int rate, std::unique_ptr<AudioSink> out);
        Canceller(const Canceller&) = delete;
        Canceller& operator=(const Canceller&) = delete;

        /// Where the capture of everything goes.
        AudioSink& Everything() { return everythingInput_; }
        /// Where the capture of an app to take out goes (made on first use).
        AudioSink& Reference(DWORD pid);
        void RemoveReference(DWORD pid);
        /// Subtracts and passes on what's been captured up to a moment ago. Call regularly.
        void Process();
        /// Tests: the offset an app was found at, or INT64_MIN while it isn't.
        int64_t OffsetOf(DWORD pid);

    private:
        struct Stream
        {
            std::deque<float> samples;  // interleaved stereo
            int64_t base = 0;           // frame index of samples.front()
            int64_t anchorIndex = -1;   // a frame whose capture time is known...
            int64_t anchorUs = 0;       // ...and that time
            int64_t lastPushUs = 0;
            int64_t End() const { return base + static_cast<int64_t>(samples.size() / 2); }
        };
        struct Ref
        {
            DWORD pid = 0;
            Stream stream;
            bool locked = false;
            int64_t offset = 0;         // Everything's frame index = this app's frame index + offset
            int64_t nextSearchUs = 0, nextCheckUs = 0;
            bool failureLogged = false;
            int64_t sinceUs = 0;        // when it was last found, or its capture started, not found
            bool waitLogged = false;
        };
        class Input final : public AudioSink
        {
        public:
            Input(Canceller& owner, DWORD pid) : owner_(owner), pid_(pid) {}
            void Push(const float* data, uint32_t frames, int64_t timeUs, bool restart) override { owner_.Append(pid_, data, frames, timeUs, restart); }

        private:
            Canceller& owner_;
            DWORD pid_; // 0 = everything
        };

        void Append(DWORD pid, const float* data, uint32_t frames, int64_t timeUs, bool restart);
        void Search(Ref& ref, int64_t nowUs);
        void Check(Ref& ref, int64_t nowUs);
        /// How much of the app's sound over its frames [j0, j1) is in everything at an offset: 1 when it's all there,
        /// about 0 when it isn't. Compares changes from frame to frame, which pins the exact frame down.
        double Match(const Stream& app, int64_t j0, int64_t j1, int64_t offset) const;
        /// Both channels added, for frames [from, to) (which must be held).
        std::vector<float> Mono(const Stream& s, int64_t from, int64_t to) const;
        int64_t TimeToIndex(const Stream& s, int64_t timeUs) const;
        int64_t TimeOf(const Stream& s, int64_t index) const;

        const int rate_;
        std::unique_ptr<AudioSink> out_;
        Input everythingInput_;
        std::mutex lock_;
        Stream everything_;
        int64_t emitted_ = -1;          // next frame of everything to pass on
        bool restartOut_ = true;
        std::vector<float> outBuffer_;
        std::map<DWORD, std::unique_ptr<Ref>> refs_;
        std::map<DWORD, std::unique_ptr<Input>> inputs_;
    };

    enum class SourceKind { ProcessInclude, ProcessExclude, Microphone };

    /// Whether Windows can capture single apps (Windows 11, or Windows 10 build 20348 and later). Without it,
    /// Desktop records everything the speakers play and the Chat and Music tracks stay silent.
    bool PerAppCaptureAvailable();

    /// The default output's own sample rate, which per-app captures keep exactly when they're made at it.
    int DefaultOutputRate();

    /// One WASAPI capture stream at `rate` feeding a sink. Reconnects by itself if the device goes away.
    class AudioSource
    {
    public:
        AudioSource(SourceKind kind, DWORD pid, std::wstring deviceId, int rate, std::unique_ptr<AudioSink> sink);
        ~AudioSource();
        AudioSource(const AudioSource&) = delete;
        AudioSource& operator=(const AudioSource&) = delete;

    private:
        void Run();
        HRESULT CaptureOnce();

        SourceKind kind_;
        DWORD pid_;
        std::wstring deviceId_;
        int rate_;
        std::unique_ptr<AudioSink> sink_;
        bool endpointFallbackLogged_ = false;
        HANDLE stop_;
        std::thread thread_;
    };

    /// Tests: plays quiet noise from this process for a while, on the default output (returns at once).
    void PlayTestNoise(int milliseconds, float amplitude);

    /// AAC encoder for one track; turns mixed float samples into packets.
    class AudioEncoder
    {
    public:
        AudioEncoder() = default;
        ~AudioEncoder();
        bool Open(int bitrateKbps, std::string& error);
        /// Encodes interleaved stereo samples; calls sink(packet) for each packet produced.
        template <typename Sink> void Encode(const std::vector<float>& samples, Sink&& sink);
        const AVCodecContext* Context() const { return ctx_; }

    private:
        void EncodeFrame(AVFrame* frame);
        AVCodecContext* ctx_ = nullptr;
        AVFrame* frame_ = nullptr;
        AVPacket* packet_ = nullptr;
        std::vector<float> pending_;
        int64_t nextPts_ = 0;
        std::vector<AVPacket*> produced_;
    };

    template <typename Sink>
    void AudioEncoder::Encode(const std::vector<float>& samples, Sink&& sink)
    {
        pending_.insert(pending_.end(), samples.begin(), samples.end());
        const int frameSize = ctx_->frame_size;
        size_t offset = 0;
        while (pending_.size() - offset >= static_cast<size_t>(frameSize * Channels))
        {
            if (av_frame_make_writable(frame_) < 0) break;
            auto* left = reinterpret_cast<float*>(frame_->data[0]);
            auto* right = reinterpret_cast<float*>(frame_->data[1]);
            const float* src = pending_.data() + offset;
            for (int i = 0; i < frameSize; i++)
            {
                left[i] = src[i * 2];
                right[i] = src[i * 2 + 1];
            }
            frame_->pts = nextPts_;
            nextPts_ += frameSize;
            offset += static_cast<size_t>(frameSize * Channels);
            EncodeFrame(frame_);
            for (AVPacket* p : produced_) sink(p);
            produced_.clear();
        }
        pending_.erase(pending_.begin(), pending_.begin() + static_cast<std::ptrdiff_t>(offset));
    }
}
