using KSP.UI.Screens; // Required for the Stock Toolbar (ApplicationLauncher)
using System;
using System.Collections;
using UnityEngine;
using WindAPI;

namespace Variometer
{
    [KSPAddon(KSPAddon.Startup.Flight, once: false)]
    public class VariometerModule : MonoBehaviour
    {
        // --- Configuration ---
        // Lift (Climb) Settings
        private float liftThreshold = 5.0f;      // Starts beeping at +5 m/s
        private float liftMax = 15.0f;           // Max effect at +15 m/s
        private float liftMaxPitch = 1.5f;       // 150% pitch at max
        private float liftMaxBeepRate = 2.0f;    // 200% beep speed at max

        // Sink (Descend) Settings
        private float sinkThreshold = -5.0f;     // Starts tone at -5 m/s
        private float sinkMax = -15.0f;          // Max effect at -15 m/s
        private float sinkMinPitch = 0.5f;       // 50% pitch at max sink

        // Audio Settings
        private float baseVolume = 0.5f;
        private string audioClipPath = "Variometer/Sounds/tone"; // No file extension in path

        // --- State ---
        private ApplicationLauncherButton appButton = null;
        private bool isVarioActive = false;
        private AudioSource audioSource;
        private AudioClip toneClip;
        private Coroutine audioLoopCoroutine;

        // --- Lifecycle ---

        private void Awake()
        {
            LoadConfig();

            // Setup UI Events
            GameEvents.onGUIApplicationLauncherReady.Add(OnGUIAppLauncherReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(OnGUIAppLauncherDestroyed);
        }

        private void Start()
        {
            // Prepare Audio Source
            GameObject audioObj = new GameObject("VariometerAudio");
            audioObj.transform.SetParent(this.transform);

            audioSource = audioObj.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.loop = false; // We handle looping manually for control
            audioSource.spatialBlend = 0f; // 2D sound (heard everywhere)
            audioSource.volume = baseVolume;

            // Load Clip from GameDatabase
            if (GameDatabase.Instance.ExistsAudioClip(audioClipPath))
            {
                toneClip = GameDatabase.Instance.GetAudioClip(audioClipPath);
                audioSource.clip = toneClip;
            }
            else
            {
                Debug.LogError($"[Variometer] Audio clip not found at: {audioClipPath}. Ensure file exists and is .wav or .ogg");
            }

            // Start the logic loop
            audioLoopCoroutine = StartCoroutine(VariometerLogicLoop());
        }

        private void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(OnGUIAppLauncherReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(OnGUIAppLauncherDestroyed);

            if (appButton != null)
            {
                ApplicationLauncher.Instance.RemoveModApplication(appButton);
            }
        }

        // --- Toolbar Integration ---

        private void OnGUIAppLauncherReady()
        {
            if (appButton == null)
            {
                appButton = ApplicationLauncher.Instance.AddModApplication(
                    ToggleVario,  // On Click
                    ToggleVario,  // On Click (again) - simpler to use same func
                    null, null, null, null, // Hover/Enable callbacks
                    ApplicationLauncher.AppScenes.FLIGHT,
                    GameDatabase.Instance.GetTexture("Variometer/Icons/icon", false) // Load icon
                );
            }
        }

        private void OnGUIAppLauncherDestroyed()
        {
            if (appButton != null)
            {
                ApplicationLauncher.Instance.RemoveModApplication(appButton);
                appButton = null;
            }
        }

        private void ToggleVario()
        {
            isVarioActive = !isVarioActive;

            // Visual feedback on the button (optional, but nice)
            if (appButton != null)
            {
                if (isVarioActive)
                    appButton.SetTexture(GameDatabase.Instance.GetTexture("Variometer/Icons/icon_on", false));
                else
                    appButton.SetTexture(GameDatabase.Instance.GetTexture("Variometer/Icons/icon", false));
            }

            ScreenMessages.PostScreenMessage(
                isVarioActive ? "Variometer: ON" : "Variometer: OFF",
                2f,
                ScreenMessageStyle.UPPER_CENTER
            );
        }

        // --- Core Logic ---

        private IEnumerator VariometerLogicLoop()
        {
            // We track if we are currently "holding" the sink tone to manage transitions
            bool isPlayingSink = false;

            while (true)
            {
                // 1. Basic Checks
                if (!isVarioActive || !FlightGlobals.ready || FlightGlobals.ActiveVessel == null || WindManager.Instance == null || toneClip == null)
                {
                    if (audioSource.isPlaying) audioSource.Stop();
                    isPlayingSink = false;
                    yield return new WaitForSeconds(0.2f);
                    continue;
                }

                Vessel v = FlightGlobals.ActiveVessel;
                CelestialBody body = v.mainBody;

                // 2. Physics Calc
                Vector3 windVector = WindManager.Instance.GetWindAtLocation(body, v.rootPart, v.GetWorldPos3D());
                Vector3 upVector = (v.GetWorldPos3D() - body.position).normalized;
                float verticalWindSpeed = Vector3.Dot(windVector, upVector);

                // --- CASE A: LIFT (Beeping) ---
                if (verticalWindSpeed > liftThreshold)
                {
                    // If we were playing the continuous sink tone, fade it out first
                    if (isPlayingSink)
                    {
                        yield return FadeOutAndStop(0.05f);
                        isPlayingSink = false;
                    }

                    // Prepare Beep Logic
                    // We enforce loop = false because we manually control the "shots"
                    audioSource.loop = false;

                    float t = Mathf.InverseLerp(liftThreshold, liftMax, verticalWindSpeed);

                    audioSource.pitch = Mathf.Lerp(1.0f, liftMaxPitch, t);
                    float baseDuration = 0.3f;
                    float beepDuration = Mathf.Lerp(baseDuration, baseDuration / liftMaxBeepRate, t);

                    // PLAY BEEP
                    audioSource.volume = baseVolume;
                    audioSource.Play();

                    // WAIT (Beep Duration - Fade Time)
                    float fadeTime = 0.05f;
                    if (beepDuration > fadeTime)
                    {
                        yield return new WaitForSeconds(beepDuration - fadeTime);
                        // Manual micro-fade for the beep end
                        yield return FadeVolumeToZero(fadeTime);
                    }
                    else
                    {
                        yield return new WaitForSeconds(beepDuration);
                    }

                    audioSource.Stop();

                    // SILENCE GAP
                    yield return new WaitForSeconds(beepDuration);
                }
                // --- CASE B: SINK (Continuous Tone) ---
                else if (verticalWindSpeed < sinkThreshold)
                {
                    // Calculate Pitch
                    float t = Mathf.InverseLerp(sinkThreshold, sinkMax, verticalWindSpeed);
                    float targetPitch = Mathf.Lerp(1.0f, sinkMinPitch, t);
                    audioSource.pitch = targetPitch;

                    // If we weren't already playing the sink tone, start it gently
                    if (!isPlayingSink || !audioSource.isPlaying)
                    {
                        audioSource.loop = true; // Use the looping feature during sink
                        audioSource.volume = 0f; // Start silent
                        audioSource.Play();

                        // Fast fade in (0.05s) to prevent "Start Pop"
                        float timer = 0f;
                        while (timer < 0.05f)
                        {
                            timer += Time.deltaTime;
                            audioSource.volume = Mathf.Lerp(0f, baseVolume, timer / 0.05f);
                            yield return null;
                        }
                        audioSource.volume = baseVolume;
                        isPlayingSink = true;
                    }
                    else
                    {
                        // Just ensure volume is correct (in case we recovered from a fade)
                        audioSource.volume = baseVolume;
                    }

                    yield return null; // Wait for next frame
                }
                // --- CASE C: DEADZONE (Silence) ---
                else
                {
                    // If we are currently playing, we need to fade out cleanly
                    if (audioSource.isPlaying)
                    {
                        yield return FadeOutAndStop(0.05f);
                    }

                    isPlayingSink = false;
                    yield return null;
                }
            }
        }

        // --- Helpers ---

        private IEnumerator FadeOutAndStop(float duration)
        {
            yield return FadeVolumeToZero(duration);
            audioSource.Stop();
            audioSource.volume = baseVolume; // Reset for next use
        }

        private IEnumerator FadeVolumeToZero(float duration)
        {
            float startVol = audioSource.volume;
            float timer = 0f;

            while (timer < duration)
            {
                timer += Time.deltaTime;
                // Lerp from current volume down to 0
                audioSource.volume = Mathf.Lerp(startVol, 0f, timer / duration);
                yield return null;
            }
            audioSource.volume = 0f;
        }

        private void LoadConfig()
        {
            ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("VARIOMETER_SETTINGS");
            if (nodes != null && nodes.Length > 0)
            {
                ConfigNode n = nodes[0];
                n.TryGetValue("liftThreshold", ref liftThreshold);
                n.TryGetValue("liftMax", ref liftMax);
                n.TryGetValue("liftMaxPitch", ref liftMaxPitch);
                n.TryGetValue("liftMaxBeepRate", ref liftMaxBeepRate);
                n.TryGetValue("sinkThreshold", ref sinkThreshold);
                n.TryGetValue("sinkMax", ref sinkMax);
                n.TryGetValue("sinkMinPitch", ref sinkMinPitch);
                n.TryGetValue("baseVolume", ref baseVolume);

                // Optional: Allow overriding sound path
                if (n.HasValue("audioClipPath")) audioClipPath = n.GetValue("audioClipPath");

                Debug.Log($"[Variometer] Config Loaded. Lift > {liftThreshold}, Sink < {sinkThreshold}");
            }
        }
    }
}