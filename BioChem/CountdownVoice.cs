using System;
using System.IO;
using FMOD;
using FMODUnity;

namespace BioChem
{
    internal static class CountdownVoice
    {
        private const int StartSecond = 10;
        private const string FileName = "语音包-CF-生化模式倒计时_爱给网_aigei_com.mp3";
        private static bool playedThisPreparation;
        private static Sound clip;
        private static Channel voice;

        internal static void Tick()
        {
            if (!ModEntry.enabled || !NetRuntime.IsConnected || !ClassroomManager.IsSingletonActive())
            {
                Reset();
                return;
            }

            var classroom = ClassroomManager.Instance;
            if (classroom.curState != EClassroomState.Prepare || !classroom.preparing)
            {
                // 上课后继续播放，让原音轨末尾的叫声完整播完。
                playedThisPreparation = false;
                return;
            }

            // 使用和原生界面相同的取整方式，在显示 00:10 时触发一次。
            if (playedThisPreparation || (int)classroom.prepareTimeCount != StartSecond) return;
            playedThisPreparation = true;
            Play();
        }

        private static void Play()
        {
            StopVoice();
            if (!AudioManager.IsSingletonActive()) return;
            var audio = AudioManager.Instance;
            float volume = audio._mainVolum * audio.GetSFXVolume();
            if (volume <= 0f) return;

            try
            {
                var system = RuntimeManager.CoreSystem;
                if (!clip.hasHandle())
                {
                    string path = Path.Combine(ModEntry.ModDirectory, "Assets", FileName);
                    var load = system.createSound(path, MODE._2D | MODE.LOOP_OFF, out var loadedClip);
                    if (load != RESULT.OK)
                    {
                        ModEntry.Log?.Invoke("上课倒计时语音加载失败（" + load + "）");
                        return;
                    }
                    clip = loadedClip;
                }

                var result = system.playSound(clip, default(ChannelGroup), true, out voice);
                if (result == RESULT.OK) result = voice.setVolume(volume);
                if (result == RESULT.OK) result = voice.setPaused(false);
                if (result == RESULT.OK) return;
                StopVoice();
                ModEntry.Log?.Invoke("上课倒计时语音播放失败（" + result + "）");
            }
            catch (Exception e)
            {
                StopVoice();
                ModEntry.Log?.Invoke("上课倒计时语音异常：" + e.Message);
            }
        }

        private static void StopVoice()
        {
            if (voice.hasHandle()) voice.stop();
            voice = default;
        }

        private static void Reset()
        {
            StopVoice();
            playedThisPreparation = false;
        }

        internal static void Cleanup()
        {
            Reset();
            if (clip.hasHandle()) clip.release();
            clip = default;
        }
    }
}
