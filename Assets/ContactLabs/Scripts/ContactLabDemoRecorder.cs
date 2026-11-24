#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Editor only. Moves a scripted fingertip through every lab and saves first-person frames,
/// so demo pictures can be made without a headset. Add it to an object in Play Mode.
/// </summary>
public class ContactLabDemoRecorder : MonoBehaviour
{
    public string outputDir;
    public float demoGain = 1.4f;
    public int width = 2880, height = 1620;

    public static bool Done;
    public static string Error;

    ContactLabManager _m;
    Camera _cam;
    Transform _left, _right;
    Vector3 _centre, _fwd, _rightAxis;
    int _frame;
    string _lab;
    Vector3 _home, _home2, _target;
    bool _twoHands;
    readonly System.Text.StringBuilder _meta = new System.Text.StringBuilder();

    IEnumerator Start()
    {
        Done = false;
        Error = null;
        IEnumerator run = Run();
        while (true)
        {
            bool more;
            try { more = run.MoveNext(); }
            catch (Exception e) { Error = e.ToString(); Debug.LogError($"[DemoRecorder] {e}"); break; }
            if (!more) break;
            yield return run.Current;
        }
        Time.captureFramerate = 0;
        if (!string.IsNullOrEmpty(outputDir)) File.WriteAllText(Path.Combine(outputDir, "frames.csv"), _meta.ToString());
        Done = true;
        Debug.Log($"[DemoRecorder] Finished. Error: {Error ?? "none"}");
    }

    IEnumerator Run()
    {
        _m = FindAnyObjectByType<ContactLabManager>();
        if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        Directory.CreateDirectory(outputDir);
        _meta.AppendLine("lab,frame,still,gain,home_x,home_y,home2_x,home2_y,target_x,target_y,real_x,real_y,virtual_x,virtual_y,real2_x,real2_y,virtual2_x,virtual2_y");
        Time.captureFramerate = 30;
        while (Time.timeSinceLevelLoad < 1.5f) yield return null; // The manager places the lab after one second.

        _centre = Field("_centre");
        _fwd = Field("_fwd");
        _rightAxis = Field("_rightAxis");

        _right = new GameObject("DemoRightTip").transform;
        _left = new GameObject("DemoLeftTip").transform;
        Vector3 rightHome = _centre + _rightAxis * 0.12f;
        _right.position = rightHome;
        _m.debugRightTip = _right;

        var go = new GameObject("DemoCamera");
        _cam = go.AddComponent<Camera>();
        var headCam = _m.head.GetComponent<Camera>();
        _cam.clearFlags = CameraClearFlags.SolidColor;
        _cam.backgroundColor = headCam != null ? headCam.backgroundColor : new Color(0.04f, 0.045f, 0.06f);
        _cam.nearClipPlane = 0.02f;
        _cam.farClipPlane = 20f;
        _cam.fieldOfView = 40f;
        _cam.stereoTargetEye = StereoTargetEyeMask.None;
        _cam.enabled = false;
        go.transform.position = _m.head.position;

        // Panel: hands hidden (the experimental condition), a fingertip drags the gain slider.
        var panel = _m.transform.Find("LabPanel");
        _lab = "panel";
        _frame = 0;
        _home = rightHome;
        _target = panel.position;
        Frame(46f, panel.position);
        // In front of the knob, so the white fingertip dot is not hidden behind it.
        Vector3 Slider(float gain) => panel.TransformPoint(new Vector3((gain - 0.5f) / 1.5f * 0.28f - 0.14f, -0.13f, -0.017f));
        yield return Move(_right, rightHome, Slider(1f) - panel.forward * 0.08f, 20);
        yield return Move(_right, _right.position, Slider(1f), 8);
        yield return Still("0_panel_gain_start");
        yield return Move(_right, Slider(1f), Slider(demoGain), 24);
        yield return Still("0_panel_gain_set");
        yield return Move(_right, _right.position, _right.position - panel.forward * 0.08f, 8);
        yield return Move(_right, _right.position, rightHome, 20);
        _m.SetGain(demoGain);
        while (_m.handView != ContactLabManager.HandView.RealAndVirtual) _m.NextHandView();
        yield return null;

        // Three reach labs.
        Vector3[] axes = { _fwd, _rightAxis, Vector3.up };
        Vector3[] arcs = { Vector3.up, Vector3.up, -_fwd };
        for (int i = 0; i < 3; i++)
        {
            yield return GoToLab((ContactLabManager.Lab)i);
            _lab = "lab" + (i + 1);
            _frame = 0;
            Vector3 home = rightHome;
            Vector3 end = home + axes[i] * (_m.RealTravel + 0.006f);
            _home = home;
            _target = Field("_targetPos");
            Frame(0f, home, _target, _target + axes[i] * 0.05f, home - axes[i] * 0.05f);
            yield return Hold(8);
            yield return Still($"{i + 1}_1_home");
            yield return Move(_right, home, end, 36, arcs[i] * 0.02f, 0.62f, $"{i + 1}_2_reach");
            yield return Hold(3);
            yield return Still($"{i + 1}_3_contact");
            yield return Hold(10);
            yield return Move(_right, end, home, 26, arcs[i] * 0.02f);
            yield return Hold(6);
        }

        // Two-hand box.
        yield return GoToLab(ContactLabManager.Lab.BimanualBox);
        _lab = "lab4";
        _frame = 0;
        Vector3 leftHome = _centre - _rightAxis * _m.bimanualHomeOffset;
        Vector3 rightHome2 = _centre + _rightAxis * _m.bimanualHomeOffset;
        float inward = (_m.bimanualHomeOffset - _m.boxWidth * 0.5f) / _m.gain + 0.006f;
        _left.position = leftHome;
        _right.position = rightHome2;
        _m.debugLeftTip = _left;
        _home = rightHome2;
        _home2 = leftHome;
        _target = _centre;
        _twoHands = true;
        Frame(0f, leftHome - _rightAxis * 0.05f, rightHome2 + _rightAxis * 0.05f, _centre + Vector3.up * 0.1f, _centre - Vector3.up * 0.1f);
        yield return Hold(8);
        yield return Still("4_1_home");
        yield return MoveBoth(leftHome, leftHome + _rightAxis * inward, rightHome2, rightHome2 - _rightAxis * inward, 36, 0.62f, "4_2_reach");
        yield return Hold(3);
        yield return Still("4_3_contact");
        yield return Hold(10);
        yield return MoveBoth(leftHome + _rightAxis * inward, leftHome, rightHome2 - _rightAxis * inward, rightHome2, 26, -1f, null);
        yield return Hold(6);
        _m.debugLeftTip = null;
        _twoHands = false;

        // Hidden object: sweep, miss, then find it.
        _right.position = rightHome;
        yield return GoToLab(ContactLabManager.Lab.HiddenObject);
        _lab = "lab5";
        _frame = 0;
        _home = rightHome;
        yield return null;
        yield return null;
        Vector3 target = Field("_targetPos");
        _target = target;
        Frame(0f, rightHome - _fwd * 0.04f, rightHome + _fwd * 0.32f + _rightAxis * 0.14f, rightHome + _fwd * 0.32f - _rightAxis * 0.14f + Vector3.up * 0.17f);
        yield return Hold(8);
        Vector3 realTarget = rightHome + (target - rightHome) / _m.gain;
        Vector3 side = Vector3.Dot(target - rightHome, _rightAxis) > 0f ? -_rightAxis : _rightAxis;
        Vector3 miss = rightHome + _fwd * 0.15f + side * 0.09f + Vector3.up * 0.02f;
        yield return Still("5_1_home");
        yield return Move(_right, rightHome, miss, 26, Vector3.up * 0.02f, 0.9f, "5_2_search");
        yield return Move(_right, miss, realTarget, 30, Vector3.up * 0.015f);
        yield return Hold(3);
        yield return Still("5_3_contact");
        if (!_m.showHiddenObject) _m.ToggleHiddenObject();
        yield return Hold(3);
        yield return Still("5_4_contact_object_shown");
        yield return Hold(10);
        yield return Move(_right, realTarget, rightHome, 26, Vector3.up * 0.02f);
        if (_m.showHiddenObject) _m.ToggleHiddenObject();
    }

    Vector3 Field(string name)
    {
        return (Vector3)typeof(ContactLabManager).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_m);
    }

    /// <summary>Aims the camera at the middle of the points. A zero field of view fits them all with a margin.</summary>
    void Frame(float fov, params Vector3[] points)
    {
        Vector3 mid = Vector3.zero;
        foreach (var p in points) mid += p;
        mid /= points.Length;
        _cam.transform.rotation = Quaternion.LookRotation(mid - _cam.transform.position, Vector3.up);
        if (fov <= 0f)
        {
            float aspect = width / (float)height, half = 0f;
            foreach (var p in points)
            {
                Vector3 l = _cam.transform.InverseTransformPoint(p);
                half = Mathf.Max(half, Mathf.Abs(l.y / l.z), Mathf.Abs(l.x / l.z) / aspect);
            }
            fov = Mathf.Clamp(2f * Mathf.Atan(half * 1.45f) * Mathf.Rad2Deg, 30f, 80f);
        }
        _cam.fieldOfView = fov;
    }

    IEnumerator GoToLab(ContactLabManager.Lab lab)
    {
        while (_m.lab != lab) _m.NextLab();
        yield return null;
        yield return null;
    }

    IEnumerator Hold(int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            yield return null;
            Capture(null);
        }
    }

    IEnumerator Still(string name)
    {
        yield return null; // One more frame, so text set this frame is rebuilt before the capture.
        Capture(name);
    }

    IEnumerator Move(Transform tip, Vector3 from, Vector3 to, int frames, Vector3 arc = default, float stillAt = -1f, string stillName = null)
    {
        bool taken = false;
        for (int i = 1; i <= frames; i++)
        {
            float t = Mathf.SmoothStep(0f, 1f, i / (float)frames);
            tip.position = Vector3.Lerp(from, to, t) + arc * Mathf.Sin(Mathf.PI * t);
            yield return null;
            bool still = stillName != null && !taken && t >= stillAt;
            Capture(still ? stillName : null);
            taken |= still;
        }
    }

    IEnumerator MoveBoth(Vector3 leftFrom, Vector3 leftTo, Vector3 rightFrom, Vector3 rightTo, int frames, float stillAt, string stillName)
    {
        bool taken = false;
        for (int i = 1; i <= frames; i++)
        {
            float t = Mathf.SmoothStep(0f, 1f, i / (float)frames);
            Vector3 arc = Vector3.up * 0.015f * Mathf.Sin(Mathf.PI * t);
            _left.position = Vector3.Lerp(leftFrom, leftTo, t) + arc;
            _right.position = Vector3.Lerp(rightFrom, rightTo, t) + arc;
            yield return null;
            bool still = stillName != null && !taken && t >= stillAt;
            Capture(still ? stillName : null);
            taken |= still;
        }
    }

    /// <summary>Saves one frame, and one line saying where the lab's parts sit in it (pixels from the top left).</summary>
    void Capture(string stillName)
    {
        string dir = Path.Combine(outputDir, _lab);
        Directory.CreateDirectory(dir);
        Save(Path.Combine(dir, $"{++_frame:0000}.png"), width, height);

        var c = System.Globalization.CultureInfo.InvariantCulture;
        string P(Vector3 world)
        {
            Vector3 v = _cam.WorldToViewportPoint(world);
            return (v.x * width).ToString("0", c) + "," + ((1f - v.y) * height).ToString("0", c);
        }
        Vector3 Virtual(Vector3 home, Vector3 real) => home + _m.gain * (real - home);
        string second = _twoHands ? P(_left.position) + "," + P(Virtual(_home2, _left.position)) : ",,,";
        _meta.AppendLine(string.Join(",", _lab, _frame.ToString(c), stillName ?? "", _m.gain.ToString("0.00", c),
            P(_home), _twoHands ? P(_home2) : ",", P(_target), P(_right.position), P(Virtual(_home, _right.position)), second));
    }

    void Save(string path, int width, int height)
    {
        var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var request = new UniversalRenderPipeline.SingleCameraRequest { destination = rt };
        if (RenderPipeline.SupportsRenderRequest(_cam, request))
        {
            RenderPipeline.SubmitRenderRequest(_cam, request);
        }
        else
        {
            _cam.targetTexture = rt;
            _cam.Render();
            _cam.targetTexture = null;
        }
        var previous = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        tex.Apply();
        RenderTexture.active = previous;
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Destroy(tex);
        RenderTexture.ReleaseTemporary(rt);
    }
}
#endif
