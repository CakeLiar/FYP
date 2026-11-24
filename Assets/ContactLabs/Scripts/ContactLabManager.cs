using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using TMPro;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Five contact-audio labs in one scene. Hands are never rendered. In every lab the virtual
/// fingertip is home + gain * (real fingertip - home), and a beep marks virtual contact.
/// The labs differ in structure: reach axis (Z, X, Y), two hands on a box, or a hidden object.
/// A panel on the left (poked with either index fingertip) cycles labs, sets the sound origin
/// and drives the gain slider.
/// </summary>
[DefaultExecutionOrder(100)]
public class ContactLabManager : MonoBehaviour
{
    public enum Lab { ForwardReach, LateralReach, VerticalReach, BimanualBox, HiddenObject }
    public enum SoundOrigin { VirtualContact, RealFingertip, NonSpatial }
    public enum HandView { Hidden, Virtual, RealAndVirtual }

    [Header("Scene references")]
    public Transform head;
    public OVRSkeleton leftSkeleton;
    public OVRSkeleton rightSkeleton;
    public Transform leftAnchor;
    public Transform rightAnchor;
    public Material baseMaterial;
    [Tooltip("Optional stand-ins for the fingertips, for testing in the Editor without a headset.")]
    public Transform debugLeftTip;
    public Transform debugRightTip;

    [Header("Moving parts (also on the VR panel)")]
    public Lab lab = Lab.ForwardReach;
    [Range(GainMin, GainMax)] public float gain = 1.2f;
    public SoundOrigin soundOrigin = SoundOrigin.VirtualContact;
    [Tooltip("Fingertip markers. Hidden is the experimental condition; the others are for checking the lab.")]
    public HandView handView = HandView.Hidden;
    [Tooltip("Shows the object in the hidden-object lab.")]
    public bool showHiddenObject;

    [Header("Geometry, metres")]
    [Tooltip("On: the visible target stays put at every gain, and the real travel is targetDistance / gain. Off: the real travel stays at realReach and the target moves with gain.")]
    public bool fixedTarget = true;
    [Tooltip("Virtual fingertip travel from home to contact when the target is fixed.")]
    public float targetDistance = 0.25f;
    [Tooltip("Real fingertip travel from home to contact in the three reach labs when the target is not fixed.")]
    public float realReach = 0.25f;
    public float targetRadius = 0.03f;
    public float homeRadius = 0.04f;
    public float boxWidth = 0.10f;
    [Tooltip("Lateral distance from the box centre to each hand's home in the two-hand lab.")]
    public float bimanualHomeOffset = 0.22f;

    const float GainMin = 0.5f, GainMax = 2f, GainStep = 0.05f;
    const float SliderHalf = 0.14f, SliderY = -0.13f;

    static readonly Color Idle = new Color(1f, 0.55f, 0.1f);
    static readonly Color Touched = new Color(0.2f, 0.9f, 0.3f);
    static readonly Color OneHand = new Color(1f, 0.9f, 0.2f);
    static readonly Color HomeOff = new Color(0.2f, 0.7f, 1f);
    static readonly Color ButtonIdle = new Color(0.25f, 0.27f, 0.32f);
    static readonly Color ButtonHot = new Color(0.45f, 0.6f, 0.95f);
    static readonly Color VirtualTip = new Color(1f, 0.25f, 0.85f);
    static readonly Color RealTip = new Color(0.85f, 0.85f, 0.85f);
    static readonly Color DriftLine = new Color(1f, 0.9f, 0.2f);

    static readonly string[] LabTitles =
    {
        "Forward reach (Z)", "Lateral reach (X)", "Vertical reach (Y)",
        "Two-hand box (X, both hands)", "Hidden object (free 3D search)"
    };

    static readonly string[] LabHelp =
    {
        "Right fingertip on the blue dot. Reach forward to the ball. Return at the beep.",
        "Right fingertip on the blue dot. Reach right to the ball. Return at the beep.",
        "Right fingertip on the blue dot. Reach up to the ball. Return at the beep.",
        "One fingertip on each blue dot. Close both hands onto the box sides. Return at the beeps.",
        "Right fingertip on the blue dot. Sweep the space ahead until the beep. Return for a new position."
    };

    class Hand
    {
        public int side;            // -1 left, +1 right
        public Vector3 home;
        public bool armed;
        public bool touching;
        public bool tracked;
        public Vector3 real;
        public Vector3 virt;
        public Renderer homeMarker;
        public Renderer cursor;
        public Renderer virtualMarker;
        public Renderer realMarker;
        public LineRenderer driftLine;
        public AudioSource audio;
        public OVRSkeleton skeleton;
        public Transform anchor;
        public OVRInput.Controller controller;
        public Transform debugTip;
    }

    class Button
    {
        public Vector2 centre, half;
        public Renderer renderer;
        public TextMeshPro label;
        public Action onPress;
        public bool inside;
        public float lastPress = -10f;
    }

    readonly Hand _left = new Hand { side = -1, controller = OVRInput.Controller.LTouch };
    readonly Hand _right = new Hand { side = 1, controller = OVRInput.Controller.RTouch };
    readonly List<Button> _buttons = new List<Button>();

    Vector3 _fwd = Vector3.forward, _rightAxis = Vector3.right, _centre;
    bool _placed, _held;
    int _contacts;
    public int ContactCount => _contacts;
    Vector3 _targetPos, _hiddenOffset;

    Transform _panel, _knob;
    TextMeshPro _title, _help, _status, _gainLabel;
    Button _soundButton, _handsButton, _hiddenButton;
    Renderer _target, _box;
    AudioClip _beep, _tick;
    AudioSource _uiAudio;
    StreamWriter _log;

    void Start()
    {
        _left.skeleton = leftSkeleton; _left.anchor = leftAnchor;
        _right.skeleton = rightSkeleton; _right.anchor = rightAnchor;
        if (head == null && Camera.main != null) head = Camera.main.transform;

        _beep = MakeClip("ContactBeep", 0.06f, 1000f, 0.3f);
        _tick = MakeClip("PanelTick", 0.02f, 300f, 0f);
        foreach (var h in Hands())
        {
            h.audio = new GameObject(h.side < 0 ? "BeepLeft" : "BeepRight").AddComponent<AudioSource>();
            h.audio.transform.SetParent(transform, false);
            h.audio.playOnAwake = false;
            h.audio.rolloffMode = AudioRolloffMode.Logarithmic;
            h.audio.minDistance = 0.1f;
            h.audio.maxDistance = 5f;
            h.homeMarker = MakePrimitive(PrimitiveType.Sphere, transform, Vector3.zero, Vector3.one * 0.025f, HomeOff);
            h.cursor = MakePrimitive(PrimitiveType.Sphere, transform, Vector3.zero, Vector3.one * 0.008f, Color.white);
            h.cursor.enabled = false;
            h.virtualMarker = MakePrimitive(PrimitiveType.Sphere, transform, Vector3.zero, Vector3.one * 0.016f, VirtualTip);
            h.realMarker = MakePrimitive(PrimitiveType.Sphere, transform, Vector3.zero, Vector3.one * 0.016f, RealTip);
            h.driftLine = new GameObject("DriftLine").AddComponent<LineRenderer>();
            h.driftLine.transform.SetParent(transform, false);
            h.driftLine.useWorldSpace = true;
            h.driftLine.positionCount = 2;
            h.driftLine.widthMultiplier = 0.002f;
            h.driftLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            if (baseMaterial != null) h.driftLine.sharedMaterial = baseMaterial;
            h.driftLine.material.color = DriftLine;
        }
        _uiAudio = gameObject.AddComponent<AudioSource>();
        _uiAudio.playOnAwake = false;
        _uiAudio.spatialBlend = 0f;

        _target = MakePrimitive(PrimitiveType.Sphere, transform, Vector3.zero, Vector3.one * targetRadius * 2f, Idle);
        _box = MakePrimitive(PrimitiveType.Cube, transform, Vector3.zero, new Vector3(boxWidth, 0.12f, 0.12f), Idle);

        BuildPanel();
        OpenLog();
        Recenter();
        _placed = false; // Placed again once the headset reports a pose.
    }

    void Update()
    {
        // The head pose is not valid on the first frames; place the lab once it is.
        if (!_placed && head != null && (Time.timeSinceLevelLoad > 1f || head.position.sqrMagnitude > 0.01f))
        {
            Recenter();
            _placed = true;
        }

        _left.debugTip = debugLeftTip;
        _right.debugTip = debugRightTip;
        foreach (var h in Hands())
        {
            h.tracked = TryGetFingertip(h, out h.real);
            if (h.tracked) h.virt = h.home + gain * (h.real - h.home);
        }

        HandleFallbackInput();
        UpdatePanel();
        UpdateHandMarkers();

        if (lab == Lab.BimanualBox) UpdateBimanual();
        else UpdateSingleHand();
    }

    void OnDestroy()
    {
        _log?.Dispose();
    }

    IEnumerable<Hand> Hands()
    {
        yield return _left;
        yield return _right;
    }

    // ---------------------------------------------------------------- tracking

    static bool TryGetFingertip(Hand h, out Vector3 position)
    {
        if (h.debugTip != null)
        {
            position = h.debugTip.position;
            return true;
        }

        var sk = h.skeleton;
        if (sk != null && sk.IsInitialized && sk.IsDataValid && sk.Bones != null)
        {
            var type = sk.GetSkeletonType();
            bool openXr = type == OVRSkeleton.SkeletonType.XRHandLeft || type == OVRSkeleton.SkeletonType.XRHandRight;
            var tip = openXr ? OVRSkeleton.BoneId.XRHand_IndexTip : OVRSkeleton.BoneId.Hand_IndexTip;
            for (int i = 0; i < sk.Bones.Count; i++)
            {
                if (sk.Bones[i].Id == tip && sk.Bones[i].Transform != null)
                {
                    position = sk.Bones[i].Transform.position;
                    return true;
                }
            }
        }

        // Controllers stand in for fingertips in the simulator or when hands are not tracked.
        if (h.anchor != null && OVRInput.IsControllerConnected(h.controller))
        {
            position = h.anchor.position;
            return true;
        }

        position = default;
        return false;
    }

    // -------------------------------------------------------------------- labs

    Vector3 ReachAxis()
    {
        switch (lab)
        {
            case Lab.LateralReach: return _rightAxis;
            case Lab.VerticalReach: return Vector3.up;
            default: return _fwd;
        }
    }

    /// <summary>Real fingertip travel from home to contact in the reach labs, at the current gain.</summary>
    public float RealTravel => fixedTarget ? targetDistance / gain : realReach;

    /// <summary>Places homes and targets for the current lab and gain.</summary>
    void Layout()
    {
        Vector3 rightHome = _centre + _rightAxis * 0.12f;
        bool bimanual = lab == Lab.BimanualBox;
        _left.home = _centre - _rightAxis * bimanualHomeOffset;
        _right.home = bimanual ? _centre + _rightAxis * bimanualHomeOffset : rightHome;

        foreach (var h in Hands())
        {
            h.homeMarker.transform.position = h.home;
            h.armed = false;
            h.touching = false;
        }
        _left.homeMarker.enabled = bimanual;
        _held = false;

        _box.enabled = bimanual;
        _box.transform.SetPositionAndRotation(_centre, Quaternion.LookRotation(_fwd, Vector3.up));
        _box.material.color = Idle;

        _target.enabled = lab == Lab.ForwardReach || lab == Lab.LateralReach || lab == Lab.VerticalReach
                          || (lab == Lab.HiddenObject && showHiddenObject);
        _target.material.color = Idle;
        _target.transform.localScale = Vector3.one * (lab == Lab.HiddenObject ? 0.08f : targetRadius * 2f);
        if (lab == Lab.HiddenObject)
        {
            _targetPos = _right.home + (fixedTarget ? 1f : gain) * _hiddenOffset;
            _target.transform.position = _targetPos;
        }
        else if (!bimanual)
        {
            // The near surface sits where the virtual fingertip arrives after RealTravel of real movement.
            _targetPos = _right.home + ReachAxis() * (RealTravel * gain + targetRadius);
            _target.transform.position = _targetPos;
        }
    }

    void NewHiddenOffset()
    {
        _hiddenOffset = _fwd * UnityEngine.Random.Range(0.14f, 0.28f)
                        + _rightAxis * UnityEngine.Random.Range(-0.12f, 0.12f)
                        + Vector3.up * UnityEngine.Random.Range(0f, 0.15f);
    }

    void UpdateSingleHand()
    {
        var h = _right;
        if (!h.tracked) return;

        if (!h.armed)
        {
            if ((h.real - h.home).magnitude < homeRadius)
            {
                h.armed = true;
                _target.material.color = Idle;
                if (lab == Lab.HiddenObject)
                {
                    NewHiddenOffset();
                    _targetPos = h.home + (fixedTarget ? 1f : gain) * _hiddenOffset;
                    _target.transform.position = _targetPos;
                }
            }
        }
        else
        {
            float radius = lab == Lab.HiddenObject ? 0.04f : targetRadius;
            if ((h.virt - _targetPos).magnitude <= radius)
            {
                h.armed = false;
                _target.material.color = Touched;
                Contact(h, "contact");
            }
        }
        h.homeMarker.material.color = h.armed ? Touched : HomeOff;
    }

    void UpdateBimanual()
    {
        float half = boxWidth * 0.5f;
        foreach (var h in Hands())
        {
            if (!h.tracked) continue;
            Vector3 d = h.virt - _centre;
            float lateral = Vector3.Dot(d, _rightAxis) * h.side; // Distance out from the box centre on this hand's side.
            bool aligned = Mathf.Abs(Vector3.Dot(d, Vector3.up)) < 0.08f && Mathf.Abs(Vector3.Dot(d, _fwd)) < 0.08f;

            if (!h.armed && !h.touching && (h.real - h.home).magnitude < homeRadius) h.armed = true;

            if (h.armed && aligned && lateral <= half)
            {
                h.armed = false;
                h.touching = true;
                Contact(h, "contact");
            }
            else if (h.touching && lateral > half + 0.02f)
            {
                h.touching = false;
            }
            h.homeMarker.material.color = h.armed ? Touched : HomeOff;
        }

        bool both = _left.touching && _right.touching;
        if (both && !_held)
        {
            // Inter-hand distance at the moment both hands signal contact (Experiment 3 measure).
            float realGap = Vector3.Dot(_right.real - _left.real, _rightAxis);
            WriteLog("both", "hold", _right.real, _right.virt, realGap);
        }
        _held = both;
        _box.material.color = both ? Touched : (_left.touching || _right.touching ? OneHand : Idle);
    }

    void Contact(Hand h, string evt)
    {
        _contacts++;
        var src = h.audio;
        bool spatial = soundOrigin != SoundOrigin.NonSpatial;
        src.spatialBlend = spatial ? 1f : 0f;
        src.spatialize = spatial;
        src.transform.position = soundOrigin == SoundOrigin.RealFingertip ? h.real : h.virt;
        src.PlayOneShot(_beep);
        WriteLog(h.side < 0 ? "left" : "right", evt, h.real, h.virt, float.NaN);
        RefreshText();
    }

    // ------------------------------------------------------------------ controls

    public void NextLab() { SetLab(((int)lab + 1) % LabTitles.Length); }
    public void PreviousLab() { SetLab(((int)lab + LabTitles.Length - 1) % LabTitles.Length); }

    void SetLab(int index)
    {
        lab = (Lab)index;
        _contacts = 0;
        NewHiddenOffset();
        Layout();
        RefreshText();
        WriteLog("-", "lab", Vector3.zero, Vector3.zero, float.NaN);
    }

    public void NextSoundOrigin()
    {
        soundOrigin = (SoundOrigin)(((int)soundOrigin + 1) % 3);
        RefreshText();
    }

    public void NextHandView()
    {
        handView = (HandView)(((int)handView + 1) % 3);
        RefreshText();
    }

    public void ToggleHiddenObject()
    {
        showHiddenObject = !showHiddenObject;
        if (lab == Lab.HiddenObject) _target.enabled = showHiddenObject;
        RefreshText();
    }

    /// <summary>Draws the real and virtual fingertips and the line between them, when switched on.</summary>
    void UpdateHandMarkers()
    {
        foreach (var h in Hands())
        {
            // The left hand has a virtual counterpart only in the two-hand lab.
            bool used = h.tracked && (h.side > 0 || lab == Lab.BimanualBox);
            bool showVirtual = used && handView != HandView.Hidden;
            bool showReal = used && handView == HandView.RealAndVirtual;
            h.virtualMarker.enabled = showVirtual;
            h.realMarker.enabled = showReal;
            h.driftLine.enabled = showReal;
            if (!used) continue;
            h.virtualMarker.transform.position = h.virt;
            h.realMarker.transform.position = h.real;
            h.driftLine.SetPosition(0, h.real);
            h.driftLine.SetPosition(1, h.virt);
        }
    }

    public void SetGain(float value)
    {
        value = Mathf.Clamp(Mathf.Round(value / GainStep) * GainStep, GainMin, GainMax);
        if (Mathf.Approximately(value, gain)) return;
        gain = value;
        Layout();
        RefreshText();
    }

    /// <summary>Re-places the panel, homes and targets relative to where the head is now.</summary>
    public void Recenter()
    {
        Vector3 headPos = head != null ? head.position : new Vector3(0f, 1.2f, 0f);
        Vector3 f = head != null ? head.forward : Vector3.forward;
        f.y = 0f;
        _fwd = f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
        _rightAxis = Vector3.Cross(Vector3.up, _fwd);
        _centre = headPos + _fwd * 0.30f - Vector3.up * 0.30f;

        Vector3 panelPos = headPos + _fwd * 0.42f - _rightAxis * 0.36f - Vector3.up * 0.05f;
        Vector3 look = panelPos - headPos;
        look.y = 0f;
        _panel.SetPositionAndRotation(panelPos, Quaternion.LookRotation(look.normalized, Vector3.up));

        NewHiddenOffset();
        Layout();
        RefreshText();
    }

    void HandleFallbackInput()
    {
        if (OVRInput.GetDown(OVRInput.Button.One)) NextLab();
        if (OVRInput.GetDown(OVRInput.Button.Two)) NextSoundOrigin();
        if (OVRInput.GetDown(OVRInput.Button.Start)) Recenter();
        float stick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick).x;
        if (Mathf.Abs(stick) > 0.5f && Time.frameCount % 12 == 0) SetGain(gain + Mathf.Sign(stick) * GainStep);

#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb.nKey.wasPressedThisFrame) NextLab();
        if (kb.pKey.wasPressedThisFrame) PreviousLab();
        if (kb.sKey.wasPressedThisFrame) NextSoundOrigin();
        if (kb.rKey.wasPressedThisFrame) Recenter();
        if (kb.hKey.wasPressedThisFrame) NextHandView();
        if (kb.tKey.wasPressedThisFrame) ToggleHiddenObject();
        if (kb.rightBracketKey.wasPressedThisFrame) SetGain(gain + GainStep);
        if (kb.leftBracketKey.wasPressedThisFrame) SetGain(gain - GainStep);
#endif
    }

    // --------------------------------------------------------------------- panel

    void BuildPanel()
    {
        _panel = new GameObject("LabPanel").transform;
        _panel.SetParent(transform, false);
        MakePrimitive(PrimitiveType.Cube, _panel, new Vector3(0f, 0f, 0.004f), new Vector3(0.36f, 0.33f, 0.005f), new Color(0.08f, 0.09f, 0.11f));

        _title = MakeText(new Vector3(0f, 0.138f, 0f), new Vector2(0.34f, 0.03f), 0.2f, Color.white);
        _help = MakeText(new Vector3(0f, 0.098f, 0f), new Vector2(0.34f, 0.04f), 0.13f, new Color(0.8f, 0.82f, 0.86f));
        _status = MakeText(new Vector3(0f, 0.06f, 0f), new Vector2(0.34f, 0.02f), 0.11f, new Color(0.6f, 0.9f, 0.7f));

        MakeButton("PREV", new Vector2(-0.12f, 0.022f), new Vector2(0.08f, 0.036f), PreviousLab);
        MakeButton("NEXT LAB", new Vector2(0f, 0.022f), new Vector2(0.13f, 0.036f), NextLab);
        MakeButton("RECENTER", new Vector2(0.12f, 0.022f), new Vector2(0.08f, 0.036f), Recenter);
        _soundButton = MakeButton("SOUND", new Vector2(0f, -0.023f), new Vector2(0.32f, 0.034f), NextSoundOrigin);
        _handsButton = MakeButton("HANDS", new Vector2(-0.07f, -0.066f), new Vector2(0.18f, 0.034f), NextHandView);
        _hiddenButton = MakeButton("OBJECT", new Vector2(0.095f, -0.066f), new Vector2(0.13f, 0.034f), ToggleHiddenObject);

        _gainLabel = MakeText(new Vector3(0f, -0.102f, 0f), new Vector2(0.34f, 0.02f), 0.13f, Color.white);
        MakePrimitive(PrimitiveType.Cube, _panel, new Vector3(0f, SliderY, 0f), new Vector3(SliderHalf * 2f, 0.006f, 0.004f), ButtonIdle);
        _knob = MakePrimitive(PrimitiveType.Cube, _panel, new Vector3(0f, SliderY, -0.006f), new Vector3(0.018f, 0.034f, 0.012f), ButtonHot).transform;
    }

    Button MakeButton(string text, Vector2 centre, Vector2 size, Action onPress)
    {
        var b = new Button { centre = centre, half = size * 0.5f, onPress = onPress };
        b.renderer = MakePrimitive(PrimitiveType.Cube, _panel, new Vector3(centre.x, centre.y, -0.006f), new Vector3(size.x, size.y, 0.012f), ButtonIdle);
        b.label = MakeText(new Vector3(centre.x, centre.y, -0.0135f), size, 0.11f, Color.white);
        b.label.text = text;
        _buttons.Add(b);
        return b;
    }

    void UpdatePanel()
    {
        foreach (var b in _buttons)
        {
            bool inside = false;
            foreach (var h in Hands())
            {
                if (!h.tracked) continue;
                Vector3 p = _panel.InverseTransformPoint(h.real);
                if (Mathf.Abs(p.x - b.centre.x) < b.half.x && Mathf.Abs(p.y - b.centre.y) < b.half.y && p.z > -0.014f && p.z < 0.03f)
                    inside = true;
            }
            if (inside && !b.inside && Time.time - b.lastPress > 0.5f)
            {
                b.lastPress = Time.time;
                _uiAudio.PlayOneShot(_tick, 0.5f);
                b.onPress();
            }
            b.inside = inside;
            b.renderer.material.color = inside ? ButtonHot : ButtonIdle;
        }

        foreach (var h in Hands())
        {
            bool near = false;
            if (h.tracked)
            {
                Vector3 p = _panel.InverseTransformPoint(h.real);
                // The fingertip dot is shown only next to the panel, so the reach space stays hand-free.
                near = Mathf.Abs(p.x) < 0.24f && Mathf.Abs(p.y) < 0.2f && p.z > -0.12f && p.z < 0.05f;
                if (Mathf.Abs(p.y - SliderY) < 0.025f && Mathf.Abs(p.x) < SliderHalf + 0.02f && p.z > -0.02f && p.z < 0.03f)
                    SetGain(Mathf.Lerp(GainMin, GainMax, Mathf.InverseLerp(-SliderHalf, SliderHalf, p.x)));
                h.cursor.transform.position = h.real;
            }
            h.cursor.enabled = near;
        }
    }

    void RefreshText()
    {
        if (_title == null) return;
        _title.text = $"Lab {(int)lab + 1}/{LabTitles.Length}: {LabTitles[(int)lab]}";
        _help.text = LabHelp[(int)lab];
        bool reach = lab == Lab.ForwardReach || lab == Lab.LateralReach || lab == Lab.VerticalReach;
        _status.text = reach
            ? $"Contacts: {_contacts}   Real reach: {(RealTravel * 100f).ToString("0.0", CultureInfo.InvariantCulture)} cm"
            : $"Contacts: {_contacts}";
        string origin = soundOrigin == SoundOrigin.VirtualContact ? "virtual contact"
            : soundOrigin == SoundOrigin.RealFingertip ? "real fingertip" : "non-spatial";
        _soundButton.label.text = $"BEEP FROM: {origin}";
        _handsButton.label.text = handView == HandView.Hidden ? "HANDS: hidden"
            : handView == HandView.Virtual ? "HANDS: virtual" : "HANDS: real + virtual";
        _hiddenButton.label.text = showHiddenObject ? "LAB 5 OBJECT: shown" : "LAB 5 OBJECT: hidden";
        _gainLabel.text = $"GAIN {gain.ToString("0.00", CultureInfo.InvariantCulture)}";
        float x = Mathf.Lerp(-SliderHalf, SliderHalf, Mathf.InverseLerp(GainMin, GainMax, gain));
        _knob.localPosition = new Vector3(x, SliderY, -0.006f);
    }

    // ------------------------------------------------------------------- helpers

    Renderer MakePrimitive(PrimitiveType type, Transform parent, Vector3 localPos, Vector3 scale, Color color)
    {
        var go = GameObject.CreatePrimitive(type);
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = scale;
        var r = go.GetComponent<Renderer>();
        if (baseMaterial != null) r.sharedMaterial = baseMaterial;
        r.material.color = color; // Instantiates a per-object copy.
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return r;
    }

    TextMeshPro MakeText(Vector3 localPos, Vector2 size, float fontSize, Color color)
    {
        var go = new GameObject("Text");
        go.transform.SetParent(_panel, false);
        go.transform.localPosition = localPos;
        var t = go.AddComponent<TextMeshPro>();
        t.rectTransform.sizeDelta = size;
        t.fontSize = fontSize;
        t.color = color;
        t.alignment = TextAlignmentOptions.Center;
        return t;
    }

    static AudioClip MakeClip(string name, float seconds, float hz, float noise)
    {
        const int rate = 48000;
        int n = Mathf.CeilToInt(seconds * rate);
        var data = new float[n];
        int ramp = Mathf.Min(n / 2, rate / 200); // 5 ms raised-cosine edges, so the burst does not click.
        var rng = new System.Random(7);
        for (int i = 0; i < n; i++)
        {
            float env = 1f;
            if (i < ramp) env = 0.5f - 0.5f * Mathf.Cos(Mathf.PI * i / ramp);
            else if (i > n - ramp) env = 0.5f - 0.5f * Mathf.Cos(Mathf.PI * (n - i) / ramp);
            float s = Mathf.Sin(2f * Mathf.PI * hz * i / rate) * (1f - noise) + ((float)rng.NextDouble() * 2f - 1f) * noise;
            data[i] = s * env * 0.8f;
        }
        var clip = AudioClip.Create(name, n, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    void OpenLog()
    {
        try
        {
            string path = Path.Combine(Application.persistentDataPath, $"contactlabs_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            _log = new StreamWriter(path) { AutoFlush = true };
            _log.WriteLine("time_s,lab,gain,sound_origin,hand,event,real_lateral_cm,real_up_cm,real_forward_cm,virtual_lateral_cm,virtual_up_cm,virtual_forward_cm,real_hand_gap_cm");
            Debug.Log($"[ContactLabs] Logging to {path}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ContactLabs] No log file: {e.Message}");
        }
    }

    void WriteLog(string hand, string evt, Vector3 real, Vector3 virt, float gapMetres)
    {
        if (_log == null) return;
        Vector3 r = ToLabFrame(real), v = ToLabFrame(virt);
        var c = CultureInfo.InvariantCulture;
        string gap = float.IsNaN(gapMetres) ? "" : (gapMetres * 100f).ToString("0.0", c);
        _log.WriteLine(string.Join(",",
            Time.time.ToString("0.000", c), lab, gain.ToString("0.00", c), soundOrigin, hand, evt,
            r.x.ToString("0.0", c), r.y.ToString("0.0", c), r.z.ToString("0.0", c),
            v.x.ToString("0.0", c), v.y.ToString("0.0", c), v.z.ToString("0.0", c), gap));
    }

    /// <summary>Centimetres from the lab centre, as lateral (right +), up, forward.</summary>
    Vector3 ToLabFrame(Vector3 world)
    {
        Vector3 d = world - _centre;
        return new Vector3(Vector3.Dot(d, _rightAxis), d.y, Vector3.Dot(d, _fwd)) * 100f;
    }
}
