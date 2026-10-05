// Audio capture (WASAPI) and per-track mixing for the replay buffer.
#pragma once

#include "Common.h"
#include <atomic>
#include <mutex>
#include <thread>
#include <vector>

namespace qc
{
    /// Collects one output track (Desktop, Chat or Mic) on the engine timeline. Sources add samples at the
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

    enum class SourceKind { ProcessInclude, ProcessExclude, Microphone };

    /// One WASAPI capture stream feeding a TrackMixer. Reconnects by itself if the device goes away.
    class AudioSource
    {
    public:
        AudioSource(SourceKind kind, DWORD pid, std::wstring deviceId, TrackMixer& mixer);
        ~AudioSource();
        AudioSource(const AudioSource&) = delete;
        AudioSource& operator=(const AudioSource&) = delete;

    private:
        void Run();
        HRESULT CaptureOnce();

        SourceKind kind_;
        DWORD pid_;
        std::wstring deviceId_;
        TrackMixer& mixer_;
        TrackMixer::Cursor cursor_;
        bool endpointFallbackLogged_ = false;
        HANDLE stop_;
        std::thread thread_;
    };

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
