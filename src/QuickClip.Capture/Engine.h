// Replay-buffer recorder: GPU screen capture + encode, audio tracks, packets kept in RAM, saved on demand.
#pragma once

#include "Audio.h"
#include <deque>
#include <map>
#include <memory>
#include <shared_mutex>

extern "C"
{
    // Layouts are mirrored in QuickClip/Recording/CaptureEngine.cs.
    struct QcConfig
    {
        const wchar_t* monitorDevice;   // e.g. \\.\DISPLAY1
        int fps;
        int outWidth;                   // 0 = monitor's native size
        int outHeight;
        const char* encoder;            // FFmpeg encoder name, e.g. av1_amf
        int videoKbps;
        int audioKbps;
        int bufferSeconds;
        int captureCursor;
        int splitChat;                  // separate Chat track
        int splitMusic;                 // separate Music track
        int micEnabled;
        const wchar_t* micDeviceId;     // null/empty = default microphone
    };

    struct QcStats
    {
        int running;
        int width;
        int height;
        double fps;
        int64_t bufferBytes;
        double bufferSeconds;
        int64_t droppedFrames;
    };

    struct QcOutput
    {
        int adapter;
        int output;
        int left, top, right, bottom;
        wchar_t deviceName[32];
        wchar_t adapterName[128];
    };

    struct QcAudioDevice
    {
        wchar_t id[256];
        wchar_t name[256];
        int isDefault;
    };
}

namespace qc
{
    /// Encoded packets of all streams for the last N seconds.
    class PacketRing
    {
    public:
        struct Entry
        {
            AVPacket* packet;
            int stream;
            int64_t timeUs;
            bool key;
        };

        explicit PacketRing(int64_t windowUs) : windowUs_(windowUs) {}
        ~PacketRing() { Clear(); }

        void Push(AVPacket* packet, int stream, int64_t timeUs, bool key);
        /// Packets from the last video keyframe at or before `fromUs` up to `toUs` (new references).
        std::vector<Entry> Snapshot(int64_t fromUs, int64_t toUs, int64_t& startUs);
        int64_t NewestVideoUs();
        void Stats(int64_t& bytes, double& seconds);
        void Clear();

    private:
        std::mutex lock_;
        std::deque<Entry> entries_;
        int64_t bytes_ = 0;
        int64_t windowUs_;
        int64_t newestVideoUs_ = -1;
    };

    /// Codec parameters of one output stream, captured once its encoder is open.
    struct StreamInfo
    {
        AVCodecParameters* params = nullptr;
        AVRational timeBase{ 1, 1 };
        std::string title;
        ~StreamInfo() { avcodec_parameters_free(&params); }
    };

    class Engine
    {
    public:
        explicit Engine(const QcConfig& config);
        ~Engine();
        bool Start(std::string& error);
        /// Desktop records everything but `desktopExclude`'s process tree, or (when that is 0) only the trees of
        /// `desktopInclude`. Chat and Music record their apps' trees.
        void SetAudioProcesses(DWORD desktopExclude, const std::vector<DWORD>& desktopInclude,
            const std::vector<DWORD>& chat, const std::vector<DWORD>& music);
        bool Save(const std::wstring& path, int seconds, const std::wstring& title, std::string& error);
        void GetStats(QcStats& stats);

        struct Shared;

    private:
        void AudioLoop();

        std::shared_ptr<Shared> shared_;
        std::wstring monitor_;
        std::wstring micDevice_;
        bool splitChat_, splitMusic_, micEnabled_;
        int audioKbps_;

        // Audio tracks: Desktop, then Chat, Music and Mic when enabled.
        struct Track
        {
            std::string title;
            std::unique_ptr<TrackMixer> mixer;
            AudioEncoder encoder;
        };
        std::vector<std::unique_ptr<Track>> tracks_;
        int chatTrack_ = -1, musicTrack_ = -1, micTrack_ = -1;

        // App capture streams by what they record. When the apps change, only the streams that differ are
        // stopped or started, so the other tracks keep recording without a gap.
        struct SourceKey
        {
            SourceKind kind;
            DWORD pid;
            int track;
            auto operator<=>(const SourceKey&) const = default;
        };
        std::mutex sourcesLock_;
        std::map<SourceKey, std::unique_ptr<AudioSource>> sources_;
        std::unique_ptr<AudioSource> micSource_;

        std::thread videoThread_, audioThread_;
    };

    int EnumOutputs(QcOutput* outputs, int max);
    int EnumMicrophones(QcAudioDevice* devices, int max);
}
