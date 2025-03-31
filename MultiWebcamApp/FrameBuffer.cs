using System.Collections.Generic;

namespace MultiWebcamApp
{
    public class FrameBuffer
    {
        private readonly List<FrameData> _frames;
        private readonly int _maxSize = 60 * 90; // 90초 @ 60fps
        private int _playPosition;

        public FrameBuffer()
        {
            _frames = new List<FrameData>(_maxSize);
            _playPosition = 0;
        }

        public void Add(FrameData frame)
        {
            lock (_frames)
            {
                if (_frames.Count >= _maxSize)
                    _frames.RemoveAt(0);
                _frames.Add(frame);
            }
        }

        public FrameData GetFrame(int index)
        {
            lock (_frames)
            {
                if (index >= 0 && index < _frames.Count)
                    return _frames[index];
                return null;
            }
        }

        public void Clear()
        {
            lock (_frames)
            {
                _frames.Clear();
                _playPosition = 0;
            }
        }

        public int Count => _frames.Count;

        public int PlayPosition
        {
            get => _playPosition;
            set => _playPosition = Math.Clamp(value, 0, _frames.Count - 1);
        }
    }
}
