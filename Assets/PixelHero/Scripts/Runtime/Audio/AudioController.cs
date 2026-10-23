using System;
using System.Collections.Generic;
using PixelHero.Entities;
using PixelHero.Sprites;
using PixelHero.UI;
using UnityEngine;

namespace PixelHero.Audio
{
    /// <summary>
    /// controllers/audio.ts on Unity AudioSources. Each Web Audio node the original builds becomes plain state here,
    /// and every frame turns that state into AudioSource volumes.
    /// <list type="bullet">
    /// <item>Music goes through one gain node at a time (<see cref="MusicNode"/>). A new song (or stopMusic) replaces
    /// the node: the old one fades to the minimum over 3 s and is disconnected after 3 s, and the new one fades
    /// in from 0.01.</item>
    /// <item>A sound plays at the sound volume. Each sound ducks the music at once by soundVolume × 0.25. When the
    /// sound ends, the music ramps back to the music volume, timed from the duck (Web Audio ramps start at the previous
    /// event), so it is reached 0.5 s after the end.</item>
    /// <item>A sound with a target (outside low power mode) goes through a PannerNode: linear distance model,
    /// refDistance one tile, maxDistance ten tiles, measured from the listener (the player's position, kept by
    /// updatePlayerListener). The original's listener orientation leaves the azimuth at 0, so the equal-power
    /// panner sends a mono sound at cos(π/4) to each side and does not pan.</item>
    /// <item>The original loads files with fetch, so a missing file plays nothing (the promise rejects).</item>
    /// </list>
    /// </summary>
    public sealed class AudioController : MonoBehaviour
    {
        /// <summary>musicCrossfadeDuration, musicMinGain and musicMaxGain.</summary>
        const double CrossfadeDuration = 3, MinGain = 0.01, MaxGain = 0.5;

        sealed class MusicNode
        {
            public readonly GainParam Gain = new GainParam(MinGain);
            public bool Connected;
            public AudioSource Source;
        }

        sealed class Sound
        {
            public AudioSource Source;
            public double End;
            /// <summary>The PannerNode's position (null when the sound is not spatial).</summary>
            public Vector2? Position;
            public double Gain;
            public Action Ended;
        }

        SpriteCatalog _catalog;
        World _world;
        int _tileSize = 16;

        MusicNode _music;
        bool _musicConnected;
        readonly List<(MusicNode node, double disconnectAt)> _fading = new List<(MusicNode, double)>();
        readonly List<Sound> _sounds = new List<Sound>();
        readonly Stack<AudioSource> _pool = new Stack<AudioSource>();
        Vector2 _listener;

        /// <summary>The music that is playing (null when none): for tests.</summary>
        public string CurrentMusic { get; private set; }

        /// <summary>The volume the current music source plays at now: for tests.</summary>
        public float MusicVolumeNow => _music?.Source != null ? _music.Source.volume : 0;

        /// <summary>Every sound started, in order: for tests.</summary>
        public readonly List<string> Played = new List<string>();

        /// <summary>The context's currentTime.</summary>
        static double Now => Time.unscaledTimeAsDouble;

        public void Init(SpriteCatalog catalog, World world)
        {
            _catalog = catalog;
            _world = world;
            _tileSize = world.TileSize;
            world.PlaySound = PlaySound;
            world.PlayMusic = PlayMusic;
            world.StopMusic = StopMusic;
            // Unity plays nothing without an AudioListener (the browser always has its destination).
            if (FindAnyObjectByType<AudioListener>() == null) gameObject.AddComponent<AudioListener>();
        }

        static float SoundVolume => GameSettings.SoundVolume / 100f;
        static double MusicVolume => GameSettings.MusicVolume / 100.0 * MaxGain * 2;

        public void PlaySound(string sound, Entity target)
        {
            if (!GameSettings.AudioEnabled) return;

            // bufferSource: updateVolume runs before the file loads.
            UpdateVolume();
            var clip = _catalog.GetSound(sound);
            if (clip == null) return;

            var entry = new Sound { Source = Take(clip), Gain = SoundVolume, End = Now + clip.length };
            if (target != null && !GameSettings.LowPowerMode)
            {
                entry.Position = new Vector2((float)target.X, (float)target.Y);
                if (clip.channels == 1) entry.Gain *= Math.Cos(Math.PI / 4);
            }

            var gain = _music.Gain;
            var now = Now;
            gain.CancelScheduledValues(now, now);
            RampGain(gain, gain.Value(now) - SoundVolume * MaxGain / 2);
            entry.Ended = () => RampGain(gain, MusicVolume, 0.5);

            _sounds.Add(entry);
            Apply(entry);
            entry.Source.Play();
            Played.Add(sound);
        }

        public void PlayMusic(string music)
        {
            if (string.IsNullOrEmpty(music))
            {
                StopMusic();
                return;
            }

            UpdateVolume();
            var clip = _catalog.GetMusic(music);
            if (clip == null) return;

            StopMusic();
            _music.Source = Take(clip);
            _music.Source.loop = true;
            if (GameSettings.AudioEnabled) _music.Connected = true;

            RampGain(_music.Gain, MusicVolume, CrossfadeDuration);
            _music.Source.volume = 0;
            _music.Source.Play();
            CurrentMusic = music;
        }

        /// <summary>updateVolume: (dis)connects the music node and ramps it to the music volume.</summary>
        public void UpdateVolume()
        {
            if (_music == null) _music = new MusicNode();

            var volume = MusicVolume;
            var now = Now;
            if (volume <= 0 || !GameSettings.AudioEnabled)
            {
                if (_musicConnected)
                {
                    _music.Connected = false;
                    _musicConnected = false;
                }
            }
            else
            {
                _music.Gain.CancelScheduledValues(now, now);

                if (_musicConnected) RampGain(_music.Gain, volume);
                else
                {
                    _music.Connected = true;
                    _musicConnected = true;
                    RampGain(_music.Gain, MinGain);
                    RampGain(_music.Gain, volume, CrossfadeDuration);
                }
            }
        }

        public void StopMusic()
        {
            var old = _music;
            _music = new MusicNode();
            CurrentMusic = null;
            if (old == null) return;

            var now = Now;
            old.Gain.CancelScheduledValues(now, now);
            RampGain(old.Gain, MinGain, CrossfadeDuration);
            _fading.Add((old, now + CrossfadeDuration));
        }

        /// <summary>rampGain: a linear ramp to the volume (ignored at zero or below), instant without a duration.</summary>
        static void RampGain(GainParam gain, double volume, double duration = 0)
        {
            if (volume > 0) gain.LinearRampToValueAtTime(volume, Now + duration, Now);
        }

        AudioSource Take(AudioClip clip)
        {
            var source = _pool.Count > 0 ? _pool.Pop() : gameObject.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = false;
            source.playOnAwake = false;
            source.spatialBlend = 0;
            return source;
        }

        void Release(AudioSource source)
        {
            if (source == null) return;
            source.Stop();
            source.clip = null;
            _pool.Push(source);
        }

        void Apply(Sound sound)
        {
            var gain = sound.Gain;
            if (sound.Position is Vector2 position)
            {
                // PannerNode, distanceModel 'linear' (rolloffFactor 1).
                double reference = _tileSize, max = _tileSize * 10;
                var distance = Math.Clamp(Vector2.Distance(position, _listener), reference, max);
                gain *= 1 - (distance - reference) / (max - reference);
            }
            sound.Source.volume = (float)gain;
        }

        void Update()
        {
            if (_world == null) return;
            var now = Now;

            // updater.ts updateSounds → updatePlayerListener.
            if (GameSettings.AudioEnabled && !GameSettings.LowPowerMode && _world.Player != null)
                _listener = new Vector2((float)_world.Player.X, (float)_world.Player.Y);

            for (var i = _sounds.Count - 1; i >= 0; i--)
            {
                var sound = _sounds[i];
                if (now < sound.End && sound.Source.isPlaying)
                {
                    Apply(sound);
                    continue;
                }

                _sounds.RemoveAt(i);
                Release(sound.Source);
                sound.Ended();
            }

            for (var i = _fading.Count - 1; i >= 0; i--)
            {
                var (node, disconnectAt) = _fading[i];
                if (now >= disconnectAt)
                {
                    _fading.RemoveAt(i);
                    Release(node.Source);
                    continue;
                }
                if (node.Source != null) node.Source.volume = node.Connected ? (float)node.Gain.Value(now) : 0;
            }

            if (_music?.Source != null) _music.Source.volume = _music.Connected ? (float)_music.Gain.Value(now) : 0;
        }
    }
}
