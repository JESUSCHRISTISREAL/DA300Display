#include <SDK/foobar2000.h>
#include <windows.h>
#include <math.h>

DECLARE_COMPONENT_VERSION("DA-300 Display meter bridge", "1.0.0",
    "Publishes foobar2000 playback peak levels to DA-300 Display (Denon DA-300USB status panel),\n"
    "so its level meter and clip LED work in WASAPI exclusive and ASIO modes.");
VALIDATE_COMPONENT_FILENAME("foo_da300display.dll");

namespace {
    struct Shm { uint32_t magic; volatile uint32_t seq; float l, r; uint32_t clipTick, updTick, playing, rate; };
    const float kClip = 0.9772f; // -0.2 dBFS, same threshold as DA-300 Display's loopback meter

    HANDLE g_map = nullptr; Shm* g_shm = nullptr; UINT_PTR g_timer = 0;
    visualisation_stream::ptr g_vis; double g_last = -1;

    void CALLBACK Tick(HWND, UINT, UINT_PTR, DWORD) {
        if (!g_shm) return;
        auto pc = playback_control::get();
        bool playing = pc->is_playing() && !pc->is_paused();
        float l = 0, r = 0; uint32_t rate = 0;
        if (playing) {
            try { if (g_vis.is_empty()) visualisation_manager::get()->create_stream(g_vis, 0); } catch (...) {}
            double t;
            if (g_vis.is_valid() && g_vis->get_absolute_time(t)) {
                double from = (g_last > 0 && t > g_last && t - g_last < 0.25) ? g_last : t - 0.04;
                audio_chunk_impl ch;
                if (t > from && g_vis->get_chunk_absolute(ch, from, t - from)) {
                    unsigned n = ch.get_channel_count(); size_t frames = ch.get_sample_count(); const audio_sample* d = ch.get_data(); rate = ch.get_sample_rate();
                    if (n > 0 && d) for (size_t i = 0; i < frames; i++) {
                        float a = fabsf((float)d[i * n]); if (a > l) l = a;
                        if (n > 1) { float b = fabsf((float)d[i * n + 1]); if (b > r) r = b; }
                    }
                    if (n == 1) r = l;
                }
                g_last = t;
            }
        } else { g_last = -1; }
        DWORD now = GetTickCount();
        g_shm->seq++;
        g_shm->l = l; g_shm->r = r; g_shm->playing = playing ? 1 : 0; g_shm->rate = rate;
        if (l >= kClip || r >= kClip) g_shm->clipTick = now;
        g_shm->updTick = now;
        g_shm->seq++;
    }

    class bridge : public initquit {
    public:
        void on_init() override {
            g_map = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(Shm), L"Local\\DA300DisplayFoobarMeter");
            if (g_map) { g_shm = (Shm*)MapViewOfFile(g_map, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(Shm)); if (g_shm) { ZeroMemory(g_shm, sizeof(Shm)); g_shm->magic = 0x44544D31; } }
            g_timer = SetTimer(nullptr, 0, 30, Tick);
        }
        void on_quit() override {
            if (g_timer) { KillTimer(nullptr, g_timer); g_timer = 0; }
            g_vis.release();
            if (g_shm) { UnmapViewOfFile(g_shm); g_shm = nullptr; }
            if (g_map) { CloseHandle(g_map); g_map = nullptr; }
        }
    };
    static initquit_factory_t<bridge> g_bridge_factory;
}
