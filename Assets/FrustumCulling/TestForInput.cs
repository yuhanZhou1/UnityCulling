using UnityEngine;
using UnityEngine.Rendering.Universal;

public class TestForInput : MonoBehaviour
{

    public bool isOpen = true;
    public float f_UpdateInterval = 0.5F;
    private float f_LastInterval;
    private int i_Frames = 0;
    private float f_Fps;

    private GameObject m_object = null;
    private string textFieldString = "text field";

    private bool isPopen = false;
    private int num_Particle = 0;
    private int num_ParticleSystem = 0;
    ParticleSystem[] ParticleSystems;

    private bool isLopen = false;
    private int num_Light = 0;
    private int num_Light_rt = 0;
    private int num_Light_baked = 0;
    private int num_Light_mixed = 0;
    private Light[] Lights;

    void BtnCloseObject(string text)
    {
        Debug.Log("Text : " + text);
        if (m_object == null || m_object.name != text)
        {
            if (m_object = GameObject.Find(text))
            {
                Debug.Log("111111" + m_object.name);
            }
            else
            {
                Debug.Log("There is no object: " + m_object.name);
            }
        }

        if (m_object != null)
        {
            Debug.Log("222222");
            m_object.SetActive(!m_object.activeSelf);
        }
    }

    void OnGUI()
    {

        GUILayout.Label($"<size=50>FPS: {f_Fps.ToString("f2")}</size>");
        GUILayout.Label($"<size=50>API: {SystemInfo.graphicsDeviceType.ToString()}</size>");
        if (GUILayout.Button("<size=40>Function</size>"))
        {
            if (isPopen)
                isPopen = false;
            else
                isPopen = true;
        }

        if (isPopen)
        {

            textFieldString = GUI.TextField(new Rect(25, 450, 100, 100), textFieldString);
            if (GUILayout.Button("<size=40>CloseObject</size>"))
            {
                BtnCloseObject(textFieldString);
            }
            if (GUILayout.Button("<size=40>60FPS</size>"))
            {
                Application.targetFrameRate = 60;
            }
            if (GUILayout.Button("<size=40>MainCameraPP_On</size>"))
            {
                var MainCamera = GameObject.Find("MainCamera");
                var cpp = MainCamera.gameObject.GetComponent<UniversalAdditionalCameraData>();
                cpp.renderPostProcessing = true;
            }
            if (GUILayout.Button("<size=40>MainCameraPP_Off</size>"))
            {
                var MainCamera = GameObject.Find("MainCamera");
                var cpp = MainCamera.gameObject.GetComponent<UniversalAdditionalCameraData>();
                cpp.renderPostProcessing = false;
            }
            if (GUILayout.Button("<size=40>OverlayCameraPP_On</size>"))
            {
                var MainCamera = GameObject.Find("Overlay1");
                var cpp = MainCamera.gameObject.GetComponent<UniversalAdditionalCameraData>();
                cpp.renderPostProcessing = true;
            }
            if (GUILayout.Button("<size=40>OverlayCameraPP_Off</size>"))
            {
                var MainCamera = GameObject.Find("Overlay1");
                var cpp = MainCamera.gameObject.GetComponent<UniversalAdditionalCameraData>();
                cpp.renderPostProcessing = false;
            }

            if (GUILayout.Button("<size=40>UICameraPP_On</size>"))
            {
                var MainCamera = GameObject.Find("UI");
                var cpp = MainCamera.gameObject.GetComponent<UniversalAdditionalCameraData>();
                cpp.renderPostProcessing = true;
            }
            if (GUILayout.Button("<size=40>UICameraPP_Off</size>"))
            {
                var MainCamera = GameObject.Find("UI");
                var cpp = MainCamera.gameObject.GetComponent<UniversalAdditionalCameraData>();
                cpp.renderPostProcessing = false;
            }
            // GUILayout.Label($"<size=50>Light数目: {num_Light.ToString("f2")} </size>");
            // GUILayout.Label($"<size=50>粒子数目: {num_Particle.ToString("f2")} " +
            //             $"粒子发射器数目: {num_ParticleSystem.ToString("f2")}</size>");
            // if (GUILayout.Button("<size=40>StopParticle</size>"))
            // {
            //     foreach (var ps in ParticleSystems)
            //     {
            //         ps.Stop();
            //         ps.Clear();
            //     }
            // }
            // if (GUILayout.Button("<size=40>PlayParticle</size>"))
            // {
            //     foreach (var ps in ParticleSystems)
            //     {
            //         ps.Play();
            //     }
            // }
        }

    }

    void GetParticle()
    {
        num_Particle = 0;
        num_ParticleSystem = 0;
        ParticleSystems = FindObjectsOfType(typeof(ParticleSystem)) as ParticleSystem[]; ;
        num_ParticleSystem = ParticleSystems.Length;

        foreach (var ps in ParticleSystems)
        {
            num_Particle += ps.particleCount;
        }
    }

    void GetLight()
    {
        num_Light_rt = 0;
        num_Light_baked = 0;
        num_Light_mixed = 0;
        Lights = FindObjectsOfType(typeof(Light)) as Light[];
        num_Light = Lights.Length;
    }

    void Update()
    {

        ++i_Frames;

        if (Time.realtimeSinceStartup > f_LastInterval + f_UpdateInterval)
        {
            f_Fps = i_Frames / (Time.realtimeSinceStartup - f_LastInterval);

            i_Frames = 0;

            f_LastInterval = Time.realtimeSinceStartup;
        }

        if (isPopen)
        {
            GetParticle();
        }

    }

}