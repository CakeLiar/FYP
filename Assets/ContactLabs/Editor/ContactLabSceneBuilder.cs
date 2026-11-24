using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Builds Assets/ContactLabs/ContactLabs.unity: the Meta camera rig, hand skeletons with no
/// hand mesh (hands stay unseen), and the ContactLabManager that creates everything else at run time.
/// </summary>
public static class ContactLabSceneBuilder
{
    public const string ScenePath = "Assets/ContactLabs/ContactLabs.unity";
    const string MaterialPath = "Assets/ContactLabs/LabUnlit.mat";
    const string RigPrefabPath = "Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab";

    [MenuItem("FYP/Build Contact Labs Scene")]
    public static string Build()
    {
        // Built additively, so the scene that is open now is left untouched.
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);

        var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigPrefabPath);
        if (rigPrefab == null)
        {
            EditorSceneManager.CloseScene(scene, true);
            return $"Missing rig prefab at {RigPrefabPath}";
        }
        var rig = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab, scene);
        var leftAnchor = rig.transform.Find("TrackingSpace/LeftHandAnchor");
        var rightAnchor = rig.transform.Find("TrackingSpace/RightHandAnchor");
        var eye = rig.transform.Find("TrackingSpace/CenterEyeAnchor");

        var cam = eye.GetComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.04f, 0.045f, 0.06f);
        cam.nearClipPlane = 0.05f;

        var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (material == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, MaterialPath);
        }

        var managerObject = new GameObject("ContactLabs");
        SceneManager.MoveGameObjectToScene(managerObject, scene);
        var manager = managerObject.AddComponent<ContactLabManager>();
        manager.head = eye;
        manager.leftAnchor = leftAnchor;
        manager.rightAnchor = rightAnchor;
        manager.baseMaterial = material;
        manager.leftSkeleton = AddHand(leftAnchor, OVRHand.Hand.HandLeft);
        manager.rightSkeleton = AddHand(rightAnchor, OVRHand.Hand.HandRight);

        // Hand tracking is off in the project config by default; the labs need bare hands.
        var config = OVRProjectConfig.CachedProjectConfig;
        if (config.handTrackingSupport == OVRProjectConfig.HandTrackingSupport.ControllersOnly)
        {
            config.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.ControllersAndHands;
            OVRProjectConfig.CommitProjectConfig(config);
        }

        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorSceneManager.CloseScene(scene, true);

        // First in the build list, so a headset build opens the labs.
        var scenes = EditorBuildSettings.scenes.Where(s => s.path != ScenePath).ToList();
        scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();
        AssetDatabase.SaveAssets();
        return $"Built {ScenePath}";
    }

    static OVRSkeleton AddHand(Transform anchor, OVRHand.Hand handType)
    {
        var go = new GameObject(handType == OVRHand.Hand.HandLeft ? "LabHandLeft" : "LabHandRight");
        go.transform.SetParent(anchor, false);

        var hand = go.AddComponent<OVRHand>();
        var handObject = new SerializedObject(hand);
        handObject.FindProperty("HandType").intValue = (int)handType;
        handObject.ApplyModifiedPropertiesWithoutUndo();

        // No OVRMesh or renderer is added: the skeleton supplies fingertip positions only.
        var skeleton = go.AddComponent<OVRSkeleton>();
        var skeletonObject = new SerializedObject(skeleton);
        var type = ((OVRSkeleton.IOVRSkeletonDataProvider)hand).GetSkeletonType();
        skeletonObject.FindProperty("_skeletonType").intValue = (int)type;
        skeletonObject.ApplyModifiedPropertiesWithoutUndo();
        return skeleton;
    }
}
