using System;
using UnityEngine;

namespace LedScreens.Media
{
    /// <summary>Something a screen can show: a GIF or image (<see cref="MediaClip"/>) or a video (<see cref="VideoMedia"/>).</summary>
    public interface IMedia : IDisposable
    {
        string Name { get; }

        float LastUsedTime { get; set; }

        /// <summary>Extra brightness, e.g. to make up for the dark gaps of the LED-dots effect.</summary>
        float BrightnessScale { get; }

        /// <summary>The texture to show at this time, or null while it isn't ready yet.</summary>
        Texture GetFrame(double time);
    }
}
