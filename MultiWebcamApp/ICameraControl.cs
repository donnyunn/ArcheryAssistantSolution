using System;

namespace MultiWebcamApp
{
    public interface ICameraControl
    {
        enum OperationMode
        {
            Idle,       // Initial state or real-time view
            Play,       // Recording/playing with delay
            Replay,     // Playback of recorded frames
            Stop        // Paused playback
        }
        OperationMode GetCurrentMode();
        void SetKey(string key);
        void SetDelay(int delaySeconds);
        void ProcessFrame(long timestamp);
    }
}
