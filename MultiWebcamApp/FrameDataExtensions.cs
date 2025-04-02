using System;
using OpenCvSharp;

namespace MultiWebcamApp
{
    /// <summary>
    /// FrameData 클래스를 위한 확장 메서드
    /// </summary>
    public static class FrameDataExtensions
    {
        /// <summary>
        /// FrameData 객체의 깊은 복사본 생성
        /// </summary>
        public static FrameData Clone(this FrameData original)
        {
            if (original == null)
                return null;

            FrameData clone = new FrameData();

            // 헤드 웹캠 복제
            if (original.WebcamHead != null && !original.WebcamHead.Empty())
            {
                clone.WebcamHead = original.WebcamHead.Clone();
            }

            // 바디 웹캠 복제
            if (original.WebcamBody != null && !original.WebcamBody.Empty())
            {
                clone.WebcamBody = original.WebcamBody.Clone();
            }

            // 압력 데이터 복제
            if (original.PressureData != null && original.PressureData.Length > 0)
            {
                clone.PressureData = new ushort[original.PressureData.Length];
                Array.Copy(original.PressureData, clone.PressureData, original.PressureData.Length);
            }

            return clone;
        }

        /// <summary>
        /// FrameData 객체의 자원 해제
        /// </summary>
        public static void Dispose(this FrameData frameData)
        {
            if (frameData == null)
                return;

            // 웹캠 이미지 해제
            frameData.WebcamHead?.Dispose();
            frameData.WebcamBody?.Dispose();

            // 기본 타입 데이터는 GC가 처리
            frameData.PressureData = null;
        }
    }
}