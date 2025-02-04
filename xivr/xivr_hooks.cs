﻿using System;
using System.IO;
using System.Drawing;
using System.Numerics;
using System.Diagnostics;
using System.Windows;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud;
using Dalamud.Game;
using Dalamud.Utility.Signatures;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Hooking;
using Dalamud.Logging;
using Dalamud.Game.ClientState.Objects.Enums;
using xivr.Structures;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using static FFXIVClientStructs.FFXIV.Client.UI.AddonNamePlate;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.Havok;

namespace xivr
{
    public delegate void HandleStatusDelegate(bool status);
    public delegate void HandleInputDelegate(InputAnalogActionData analog, InputDigitalActionData digital);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void UpdateControllerInput(ActionButtonLayout buttonId, InputAnalogActionData analog, InputDigitalActionData digital);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void InternalLogging(String value);



    [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    public sealed class HandleStatus : System.Attribute
    {
        public string fnName { get; private set; }
        public HandleStatus(string name)
        {
            fnName = name;
        }
    }

    [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    public sealed class HandleInputAttribute : System.Attribute
    {
        public ActionButtonLayout inputId { get; private set; }
        public HandleInputAttribute(ActionButtonLayout buttonId) => inputId = buttonId;
    }


    [StructLayout(LayoutKind.Explicit)]
    public unsafe struct ActorModel
    {
        [FieldOffset(0x50)] public hkQsTransformf basePosition;
        [FieldOffset(0xA0)] public Skeleton* skeleton;
    }

    static class ExtendedData
    {
        public unsafe static Character* GetCharacter(this PlayerCharacter playerCharacter)
        {
            return (Character*)playerCharacter.Address;
        }

        public unsafe static ActorModel* GetActorModel(this PlayerCharacter playerCharacter)
        {
            return *(ActorModel**)(playerCharacter.Address + 0x100);
        }
    }



    internal unsafe class xivr_hooks
    {
        protected Dictionary<string, HandleStatusDelegate> functionList = new Dictionary<string, HandleStatusDelegate>();
        protected Dictionary<ActionButtonLayout, HandleInputDelegate> inputList = new Dictionary<ActionButtonLayout, HandleInputDelegate>();

        //----
        // Required here to load openvr_api, if its not then openvr_api isnt loaded and
        // xivr_main isnt loaded either
        //----
        [DllImport("openvr_api.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern bool VR_IsHmdPresent();


        byte[] GetThreadedDataASM =
            {
                0x55, // push rbp
                0x65, 0x48, 0x8B, 0x04, 0x25, 0x58, 0x00, 0x00, 0x00, // mov rax,gs:[00000058]
                0x5D, // pop rbp
                0xC3  // ret
            };


        private bool initalized = false;
        private bool hooksSet = false;
        private bool enableVR = true;
        private bool enableFloatingHUD = true;
        private bool forceFloatingScreen = false;
        private byte targetAddonAlpha = 0;
        private CameraModes gameMode = 0;
        private int curEye = 0;
        private int[] nextEye = { 1, 0 };
        private int[] swapEyes = { 1, 0 };
        private float RadianConversion = MathF.PI / 180.0f;
        private float cameraZoom = 0.0f;
        private float leftBumperValue = 0.0f;
        private float firstPersonCameraHeight = 0.0f;
        private Vector2 rotateAmount = new Vector2(0.0f, 0.0f);
        private Vector3 onwardAngle = new Vector3(0.0f, 0.0f, 0.0f);
        private Vector3 onwardDiff = new Vector3(0.0f, 0.0f, 0.0f);
        private Point virtualMouse = new Point(0, 0);
        private Dictionary<ActionButtonLayout, bool> inputState = new Dictionary<ActionButtonLayout, bool>();
        private Dictionary<ConfigOption, int> SavedSettings = new Dictionary<ConfigOption, int>();
        private Stack<bool> overrideFromParent = new Stack<bool>();
        private bool frfCalculateViewMatrix = false; // frf first run this frame

        private const int FLAG_INVIS = (1 << 1) | (1 << 11);
        private const byte NamePlateCount = 50;
        private UInt64 BaseAddress = 0;
        private UInt64 globalScaleAddress = 0;
        private UInt64 RenderTargetManagerAddress = 0;
        private GCHandle getThreadedDataHandle;
        private int[] runCount = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        private UInt64 tls_index = 0;
        private UpdateControllerInput controllerCallback;
        private InternalLogging internalLogging;

        private Matrix4x4 curViewMatrix = Matrix4x4.Identity;
        private Matrix4x4 hmdMatrix = Matrix4x4.Identity;
        private Matrix4x4 lhcMatrix = Matrix4x4.Identity;
        private Matrix4x4 lhcMatrixI = Matrix4x4.Identity;
        private Matrix4x4 rhcMatrix = Matrix4x4.Identity;
        private Matrix4x4 rhcMatrixI = Matrix4x4.Identity;
        private Matrix4x4 fixedProjection = Matrix4x4.Identity;
        private Matrix4x4[] gameProjectionMatrix = {
                    Matrix4x4.Identity,
                    Matrix4x4.Identity
                };
        private Matrix4x4[] eyeOffsetMatrix = {
                    Matrix4x4.Identity,
                    Matrix4x4.Identity
                };

        private SceneCameraManager* camInst = null;
        private ControlSystemCameraManager* csCameraManager = null;
        private AtkTextNode* vrTargetCursor = null;
        private NamePlateObject* currentNPTarget = null;

        private static class Signatures
        {
            internal const string g_tls_index = "8B 15 ?? ?? ?? ?? 45 33 E4";
            internal const string g_TextScale = "F3 0F 10 0D ?? ?? ?? ?? F3 0F 10 40 4C";
            internal const string g_SceneCameraManagerInstance = "48 8B 05 ?? ?? ?? ?? 83 78 50 00 75 22";
            internal const string g_RenderTargetManagerInstance = "48 8B 05 ?? ?? ?? ?? 49 63 C8";
            internal const string g_ControlSystemCameraManager = "48 8D 0D ?? ?? ?? ?? F3 0F 10 4B ??";



            internal const string DisableLeftClick = "E8 ?? ?? ?? ?? BA ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 84 C0 74 16";
            internal const string DisableRightClick = "E8 ?? ?? ?? ?? 48 8B CE E8 ?? ?? ?? ?? 48 85 C0 74 1B";
            internal const string SetRenderTarget = "E8 ?? ?? ?? ?? 40 38 BC 24 00 02 00 00";
            internal const string AllocateQueueMemory = "E8 ?? ?? ?? ?? 48 85 C0 74 ?? C7 00 04 00 00 00";
            internal const string Pushback = "E8 ?? ?? ?? ?? EB ?? 8B 87 6C 04 00 00";
            internal const string PushbackUI = "E8 ?? ?? ?? ?? EB 05 E8 ?? ?? ?? ?? 48 8B 5C 24 78";
            internal const string OnRequestedUpdate = "48 8B C4 41 56 48 81 EC ?? ?? ?? ?? 48 89 58 F0";
            internal const string DXGIPresent = "E8 ?? ?? ?? ?? C6 47 79 00 48 8B 8F";
            internal const string CamManagerSetMatrix = "4C 8B DC 49 89 5B 10 49 89 73 18 49 89 7B 20 55 49 8D AB";
            internal const string CSUpdateConstBuf = "4C 8B DC 49 89 5B 20 55 57 41 56 49 8D AB";
            internal const string SetUIProj = "E8 ?? ?? ?? ?? 8B 0D ?? ?? ?? ?? 48 8D 94 24";
            internal const string CalculateViewMatrix = "E8 ?? ?? ?? ?? 8B 83 EC 00 00 00 D1 E8 A8 01 74 1B";
            internal const string UpdateRotation = "E8 ?? ?? ?? ?? 0F B6 93 20 02 00 00 48 8B CB";
            internal const string MakeProjectionMatrix2 = "E8 ?? ?? ?? ?? 4C 8B 2D ?? ?? ?? ?? 41 0F 28 C2";
            internal const string CSMakeProjectionMatrix = "E8 ?? ?? ?? ?? 0F 28 46 10 4C 8D 7E 10";
            internal const string RenderThreadSetRenderTarget = "E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? F3 41 0F 10 5A 18";
            internal const string NamePlateDraw = "0F B7 81 ?? ?? ?? ?? 4C 8B C1 66 C1 E0 06";
            internal const string RunBoneMath = "E8 ?? ?? ?? ?? 44 0F 28 58 10";
            internal const string LoadCharacter = "48 89 5C 24 10 48 89 6C 24 18 56 57 41 57 48 83 EC 30 48 8B F9 4D 8B F9 8B CA 49 8B D8 8B EA";
            internal const string ChangeEquipment = "E8 ?? ?? ?? ?? 41 B5 01 FF C6";
            internal const string ChangeWeapon = "E8 ?? ?? ?? ?? 80 7F 25 00";
            internal const string EquipGearsetInternal = "E8 ?? ?? ?? ?? C7 87 08 01 00 00 00 00 00 00 C6 46 08 01 E9 ?? ?? ?? ?? 41 8B 4E 04";
            internal const string GetAnalogueValue = "E8 ?? ?? ?? ?? 66 44 0F 6E C3";
            internal const string ControllerInput = "E8 ?? ?? ?? ?? 41 8B 86 3C 04 00 00";


            
            
        }

        public static void PrintEcho(string message) => DalamudApi.ChatGui.Print($"[xivr] {message}");
        public static void PrintError(string message) => DalamudApi.ChatGui.PrintError($"[xivr] {message}");


        public void SetFunctionHandles()
        {
            //----
            // Gets a list of all the methods this class contains that are public and instanced (non static)
            // then looks for a specific attirbute attached to the class
            // Once found, create a delegate and add both the attribute and delegate to a dictionary
            //----
            functionList.Clear();
            System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
            foreach (System.Reflection.MethodInfo method in this.GetType().GetMethods(flags))
            {
                foreach (System.Attribute attribute in method.GetCustomAttributes(typeof(HandleStatus), false))
                {
                    string key = ((HandleStatus)attribute).fnName;
                    HandleStatusDelegate handle = (HandleStatusDelegate)HandleStatusDelegate.CreateDelegate(typeof(HandleStatusDelegate), this, method);

                    if (!functionList.ContainsKey(key))
                    {
                        if (xivr.cfg.data.vLog)
                            PluginLog.Log($"SetFunctionHandles Adding {key}");
                        functionList.Add(key, handle);
                    }
                }
            }
        }


        public void SetInputHandles()
        {
            //----
            // Gets a list of all the methods this class contains that are public and instanced (non static)
            // then looks for a specific attirbute attached to the class
            // Once found, create a delegate and add both the attribute and delegate to a dictionary
            //----
            inputList.Clear();
            System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
            foreach (System.Reflection.MethodInfo method in this.GetType().GetMethods(flags))
            {
                foreach (System.Attribute attribute in method.GetCustomAttributes(typeof(HandleInputAttribute), false))
                {
                    ActionButtonLayout key = ((HandleInputAttribute)attribute).inputId;
                    HandleInputDelegate handle = (HandleInputDelegate)HandleInputDelegate.CreateDelegate(typeof(HandleInputDelegate), this, method);

                    if (!inputList.ContainsKey(key))
                    {
                        if (xivr.cfg.data.vLog)
                            PluginLog.Log($"SetInputHandles Adding {key}");
                        inputList.Add(key, handle);
                        inputState.Add(key, false);
                    }
                }
            }
        }

        public bool SetupVRTargetCursor()
        {
            if(vrTargetCursor != null)
            {
                return true;
            }

            vrTargetCursor = (AtkTextNode*)IMemorySpace.GetUISpace()->Malloc((ulong)sizeof(AtkTextNode), 8);
            if (vrTargetCursor == null)
            {
                PluginLog.Debug("Failed to allocate memory for text node");
                return false;
            }
            IMemorySpace.Memset(vrTargetCursor, 0, (ulong)sizeof(AtkTextNode));
            vrTargetCursor->Ctor();

            vrTargetCursor->AtkResNode.Type = NodeType.Text;
            vrTargetCursor->AtkResNode.Flags = (short)(NodeFlags.UseDepthBasedPriority);
            vrTargetCursor->AtkResNode.DrawFlags = 12;

            vrTargetCursor->LineSpacing = 12;
            vrTargetCursor->AlignmentFontType = 4;
            vrTargetCursor->FontSize = (byte)xivr.cfg.data.targetCursorSize;
            vrTargetCursor->TextFlags = (byte)(TextFlags.AutoAdjustNodeSize | TextFlags.Edge);
            vrTargetCursor->TextFlags2 = 0;

            vrTargetCursor->SetText("↓");

            vrTargetCursor->AtkResNode.ToggleVisibility(true);

            vrTargetCursor->AtkResNode.SetPositionShort(90, -23);
            ushort outWidth = 0;
            ushort outHeight = 0;
            vrTargetCursor->GetTextDrawSize(&outWidth, &outHeight);
            vrTargetCursor->AtkResNode.SetWidth((ushort)(outWidth));
            vrTargetCursor->AtkResNode.SetHeight((ushort)(outHeight));

            // white fill
            vrTargetCursor->TextColor.R = 255;
            vrTargetCursor->TextColor.G = 255;
            vrTargetCursor->TextColor.B = 255;
            vrTargetCursor->TextColor.A = 255;

            // yellow/golden glow
            vrTargetCursor->EdgeColor.R = 235;
            vrTargetCursor->EdgeColor.G = 185;
            vrTargetCursor->EdgeColor.B = 7;
            vrTargetCursor->EdgeColor.A = 255;

            return true;
        }

        public void FreeVRTargetCursor()
        {
            if(vrTargetCursor != null)
            {
                if (currentNPTarget != null)
                    RemoveVRCursor(currentNPTarget);

                currentNPTarget = null;

                vrTargetCursor->AtkResNode.Destroy(true);
                vrTargetCursor = null;
            }
        }

        public void AddVRCursor(NamePlateObject* nameplate)
        {
            if(nameplate != null && vrTargetCursor != null)
            {
                var npComponent = nameplate->RootNode->Component;

                var lastChild = npComponent->UldManager.RootNode;
                while (lastChild->PrevSiblingNode != null) lastChild = lastChild->PrevSiblingNode;

                lastChild->PrevSiblingNode = (AtkResNode*)vrTargetCursor;
                vrTargetCursor->AtkResNode.NextSiblingNode = lastChild;
                vrTargetCursor->AtkResNode.ParentNode = (AtkResNode*)nameplate->RootNode;

                npComponent->UldManager.UpdateDrawNodeList();
            }
        }

        public void RemoveVRCursor(NamePlateObject* nameplate)
        {
            if (nameplate != null && vrTargetCursor != null)
            {
                var npComponent = nameplate->RootNode->Component;

                var lastChild = npComponent->UldManager.RootNode;
                while (lastChild->PrevSiblingNode != null) lastChild = lastChild->PrevSiblingNode;

                if(lastChild == vrTargetCursor)
                {
                    lastChild->NextSiblingNode->PrevSiblingNode = null;
                   
                    vrTargetCursor->AtkResNode.NextSiblingNode = null;
                    vrTargetCursor->AtkResNode.ParentNode = null;

                    npComponent->UldManager.UpdateDrawNodeList();
                }
                else
                {
                    PluginLog.Error("RemoveVRCursor: lastChild != vrTargetCursor");
                }
            }
        }

        public void UpdateVRCursorSize()
        {
            if (vrTargetCursor == null) return;

            vrTargetCursor->FontSize = (byte)xivr.cfg.data.targetCursorSize;
            ushort outWidth = 0;
            ushort outHeight = 0;
            vrTargetCursor->GetTextDrawSize(&outWidth, &outHeight);
            vrTargetCursor->AtkResNode.SetWidth(outWidth);
            vrTargetCursor->AtkResNode.SetHeight(outHeight);

            // explanation of these numbers
            // Some setup info:
            // 1. The ↓ character output from GetTextDrawSize is always 1:1 with the
            //    requested font. Font size 100 results in outWidth 100 and outHeight 100.
            // 2. The anchor point for text fields are the upper left corner of the frame.
            // 3. The hand-tuned position of the default font size 100 is x 90, y -23.
            // 
            // Adding the inverted delta offset (and div by 2 for x) correctly moves the ancor
            // from upper left to bottom center. However I noticed that as the font scales
            // up and down, the point of the arrow drifts slightly along the x and y. This
            // is the reason for the * 1.10 and * 1.15. This corrects for the drift and keeps
            // the point of the arrow exactly where it should be.

            const float DriftOffset_X = 1.10f;
            const float DriftOffset_Y = 1.15f;

            short xpos = (short)(90 + ((100 - outWidth) / 2 * DriftOffset_X));
            short ypos = (short)(-23 + (100 - outWidth) * DriftOffset_Y);
            vrTargetCursor->AtkResNode.SetPositionShort(xpos, ypos);
        }

        public void SetVRCursor(NamePlateObject* nameplate)
        {
            // nothing to do!
            if (currentNPTarget == nameplate)
                return;

            if(vrTargetCursor != null)
            {
                if(currentNPTarget != null)
                {
                    RemoveVRCursor(currentNPTarget);
                    currentNPTarget = null;
                }

                if(nameplate != null)
                {
                    AddVRCursor(nameplate);
                    currentNPTarget = nameplate;
                }
            }
        }

        public bool Initialize()
        {
            if (xivr.cfg.data.vLog)
                PluginLog.Log($"Initialize A {initalized} {hooksSet}");

            if (initalized == false)
            {
                SignatureHelper.Initialise(this);

                BaseAddress = (UInt64)Process.GetCurrentProcess()?.MainModule?.BaseAddress;

                IntPtr tmpAddress = DalamudApi.SigScanner.GetStaticAddressFromSig(Signatures.g_SceneCameraManagerInstance);
                camInst = (SceneCameraManager*)(*(UInt64*)tmpAddress);

                tmpAddress = DalamudApi.SigScanner.GetStaticAddressFromSig(Signatures.g_ControlSystemCameraManager);
                csCameraManager = (ControlSystemCameraManager*)tmpAddress;

                globalScaleAddress = (UInt64)DalamudApi.SigScanner.GetStaticAddressFromSig(Signatures.g_TextScale);
                RenderTargetManagerAddress = (UInt64)DalamudApi.SigScanner.GetStaticAddressFromSig(Signatures.g_RenderTargetManagerInstance);
                tls_index = (UInt64)DalamudApi.SigScanner.GetStaticAddressFromSig(Signatures.g_tls_index);

                GetThreadedDataInit();
                SetFunctionHandles();
                SetInputHandles();

                controllerCallback = (buttonId, analog, digital) =>
                {
                    if (inputList.ContainsKey(buttonId))
                        inputList[buttonId](analog, digital);
                };


                internalLogging = (value) =>
                {
                    PluginLog.Log($"xivr_main: {value}");
                };

                Imports.SetLogFunction(internalLogging);

                initalized = true;
            }
            if (xivr.cfg.data.vLog)
                PluginLog.Log($"Initialize B {initalized} {hooksSet}");

            return initalized;
        }

        public bool Start()
        {
            if(xivr.cfg.data.vLog)
                PluginLog.Log($"Start A {initalized} {hooksSet}");
            if (initalized == true && hooksSet == false && VR_IsHmdPresent())
            {
                if (xivr.cfg.data.vLog)
                    PluginLog.Log($"SetDX Dx: {(IntPtr)Device.Instance():X} RndTrg:{*(IntPtr*)RenderTargetManagerAddress:X}");

                if (!Imports.SetDX11((IntPtr)Device.Instance(), *(IntPtr*)RenderTargetManagerAddress))
                    return false;

                string filePath = Path.Join(DalamudApi.PluginInterface.AssemblyLocation.DirectoryName, "config", "actions.json");
                if (Imports.SetActiveJSON(filePath, filePath.Length) == false)
                    PluginLog.LogError($"Error loading Json file : {filePath}");

                gameProjectionMatrix[0] = Matrix4x4.Transpose(Imports.GetFramePose(poseType.Projection, 0));
                gameProjectionMatrix[1] = Matrix4x4.Transpose(Imports.GetFramePose(poseType.Projection, 1));
                gameProjectionMatrix[0].M43 *= -1;
                gameProjectionMatrix[1].M43 *= -1;

                //----
                // Enable all hooks
                //----
                foreach (KeyValuePair<string, HandleStatusDelegate> attrib in functionList)
                    attrib.Value(true);

                hooksSet = true;
                PrintEcho("Starting VR.");
            }
            if (xivr.cfg.data.vLog)
                PluginLog.Log($"Start B {initalized} {hooksSet}");
            return hooksSet;
        }

        public void Stop()
        {
            if (xivr.cfg.data.vLog)
                PluginLog.Log($"Stop A {initalized} {hooksSet}");
            if (hooksSet == true)
            {
                //----
                // Disable all hooks
                //----
                foreach (KeyValuePair<string, HandleStatusDelegate> attrib in functionList)
                    attrib.Value(false);

                gameProjectionMatrix[0] = Matrix4x4.Identity;
                gameProjectionMatrix[1] = Matrix4x4.Identity;
                eyeOffsetMatrix[0] = Matrix4x4.Identity;
                eyeOffsetMatrix[1] = Matrix4x4.Identity;

                FreeVRTargetCursor();

                //----
                // Restores the modified clothing when disabling vr
                //----
                PlayerCharacter? player = DalamudApi.ClientState.LocalPlayer;
                if (player != null)
                {
                 }

                FirstToThirdPersonView();



                //----
                // Restores the target arrow alpha
                //----
                AtkUnitBase* targetAddon = (AtkUnitBase*)DalamudApi.GameGui.GetAddonByName("_TargetCursor", 1);
                if (targetAddon != null)
                    targetAddon->Alpha = targetAddonAlpha;

                Imports.UnsetDX11();

                hooksSet = false;
                PrintEcho("Stopping VR.");
            }
            if (xivr.cfg.data.vLog)
                PluginLog.Log($"Stop B {initalized} {hooksSet}");
        }

        private void FirstToThirdPersonView()
        {
            PlayerCharacter? player = DalamudApi.ClientState.LocalPlayer;
            if (player != null)
            {
                ActorModel* actorModel = player.GetActorModel();
                if (actorModel != null)
                {
                    Skeleton* skeleton = actorModel->skeleton;
                    if (skeleton != null)
                    {
                        for (ushort p = 0; p < skeleton->PartialSkeletonCount; p++)
                        {
                            hkaPose* playerPose = skeleton->PartialSkeletons[p].GetHavokPose(0);
                            if (playerPose == null) continue;

                            for (int i = 0; i < playerPose->Skeleton->Bones.Length; i++)
                            {
                                string boneName = playerPose->Skeleton->Bones[i].Name.String;
                                if (BoneParentOverrideList.ContainsKey(boneName))
                                    playerPose->Skeleton->ParentIndices[i] = BoneParentOverrideList[boneName].Value;
                            }
                        }
                        BoneParentOverrideList.Clear();
                    }
                }

                Character* playerChar = player.GetCharacter();
                UInt64 equipOffset = ((UInt64)playerChar) + 0x6D0;
                ChangeEquipmentHook!.Original(equipOffset, CharEquipSlots.Head, currentEquipmentSet.Head);
                ChangeEquipmentHook!.Original(equipOffset, CharEquipSlots.Body, currentEquipmentSet.Body);
                ChangeEquipmentHook!.Original(equipOffset, CharEquipSlots.Hands, currentEquipmentSet.Hands);
                ChangeEquipmentHook!.Original(equipOffset, CharEquipSlots.Ears, currentEquipmentSet.Ears);

                ChangeWeaponHook!.Original(equipOffset, CharWeaponSlots.MainHand, currentWeaponSet.MainHand, 0, 1, 0, 0);
                ChangeWeaponHook!.Original(equipOffset, CharWeaponSlots.OffHand, currentWeaponSet.OffHand, 0, 1, 0, 0);
                ChangeWeaponHook!.Original(equipOffset, CharWeaponSlots.uk3, currentWeaponSet.Uk3, 0, 1, 0, 0);

                haveSavedEquipmentSet = false;
            }
        }


        int timer = 100;
        CharEquipSlotData hiddenEquipHead = new CharEquipSlotData(9236, 99, 0);
        CharEquipSlotData hiddenEquipEars = new CharEquipSlotData(0, 99, 0);
        CharEquipSlotData hiddenEquipBody = new CharEquipSlotData(6158, 99, 0);
        CharWeaponSlotData hiddenEquipWeaponMainHand = new CharWeaponSlotData(0, 0, 0, 0);
        CharWeaponSlotData hiddenEquipWeaponOffHand = new CharWeaponSlotData(0, 0, 0, 0);

        bool haveSavedEquipmentSet = false;
        CharEquipData currentEquipmentSet = new CharEquipData();
        CharWeaponData currentWeaponSet = new CharWeaponData();

        CameraModes oldGameMode = 0;
        bool gameModeChanged = false;
        private Dictionary<UInt64, List<KeyValuePair<Vector3, hkQsTransformf>>> boneLayout = new Dictionary<UInt64, List<KeyValuePair<Vector3, hkQsTransformf>>>();
        private Dictionary<string, KeyValuePair<int, short>> BoneParentOverrideList = new Dictionary<string, KeyValuePair<int, short>>();

        Dictionary<hkaPose, Dictionary<string, int>> boneNames = new Dictionary<hkaPose, Dictionary<string, int>>();

        public void Update(Dalamud.Game.Framework framework_)
        {
            if (hooksSet)
            {
                Imports.UpdateController(controllerCallback);
                Matrix4x4.Invert(Imports.GetFramePose(poseType.EyeOffset, 0), out eyeOffsetMatrix[0]);
                Matrix4x4.Invert(Imports.GetFramePose(poseType.EyeOffset, 1), out eyeOffsetMatrix[1]);
                Matrix4x4.Invert(Imports.GetFramePose(poseType.hmdPosition, -1), out hmdMatrix);
                lhcMatrix = Imports.GetFramePose(poseType.LeftHand, -1);
                rhcMatrix = Imports.GetFramePose(poseType.RightHand, -1);

                Matrix4x4 rot90 = Matrix4x4.CreateRotationY(90 * RadianConversion);
                lhcMatrixI = new Matrix4x4(
                        -lhcMatrix.M33,-lhcMatrix.M32, lhcMatrix.M31, lhcMatrix.M34, 
                        -lhcMatrix.M23,-lhcMatrix.M22, lhcMatrix.M21, lhcMatrix.M24,
                        -lhcMatrix.M13,-lhcMatrix.M12, lhcMatrix.M11, lhcMatrix.M14,
                         lhcMatrix.M43, lhcMatrix.M42, -lhcMatrix.M41, lhcMatrix.M44
                        );

                rhcMatrixI = new Matrix4x4(
                        -rhcMatrix.M33,-rhcMatrix.M32, rhcMatrix.M31, rhcMatrix.M34,
                        -rhcMatrix.M23,-rhcMatrix.M22, rhcMatrix.M21, rhcMatrix.M24,
                        -rhcMatrix.M13,-rhcMatrix.M12, rhcMatrix.M11, rhcMatrix.M14,
                         rhcMatrix.M43, rhcMatrix.M42, -rhcMatrix.M41, rhcMatrix.M44
                        );
                lhcMatrixI *= rot90;
                rhcMatrixI *= rot90;

                frfCalculateViewMatrix = false;

                gameModeChanged = false;
                if (oldGameMode != gameMode)
                {
                    oldGameMode = gameMode;
                    gameModeChanged = true;
                    Imports.Recenter();
                }

                boneLayout.Clear();
                PlayerCharacter? player = DalamudApi.ClientState.LocalPlayer;
                if (player != null)
                {
                    Character* playerChar = player.GetCharacter();
                    if (playerChar != null)
                    {
                        if (haveSavedEquipmentSet == false)
                        {
                            currentEquipmentSet.Save(playerChar);
                            currentWeaponSet.Save(playerChar);
                            haveSavedEquipmentSet = true;
                        }

                        UInt64 equipOffset = ((UInt64)playerChar) + 0x6D0;
                        if(equipOffset > 0 && gameMode == CameraModes.FirstPerson)
                        {
                            //----
                            // override the head and earing
                            //----
                            if (playerChar->DrawData.Head.Variant != 99)
                            {
                                ChangeEquipmentHook!.Original(equipOffset, CharEquipSlots.Head, hiddenEquipHead);
                                ChangeEquipmentHook!.Original(equipOffset, CharEquipSlots.Ears, hiddenEquipEars);
                            }

                            //----
                            // override the body if hidden
                            //----
                            if (playerChar->DrawData.Top.Variant != 99 && xivr.cfg.data.fpmShowBody != true)
                                ChangeEquipmentHook!.Original(equipOffset, CharEquipSlots.Body, hiddenEquipBody);

                            //----
                            // override the weapon
                            //----
                            if (playerChar->DrawData.MainHandModel.Id != 0)
                            {
                                ChangeWeaponHook!.Original(equipOffset, CharWeaponSlots.MainHand, hiddenEquipWeaponMainHand, 0, 1, 0, 0);
                                ChangeWeaponHook!.Original(equipOffset, CharWeaponSlots.OffHand, hiddenEquipWeaponOffHand, 0, 1, 0, 0);
                                //ChangeWeaponHook!.Original(equipOffset, CharWeaponSlots.uk3, currentWeaponSet.Uk3, 0, 1, 0, 0);
                            }
                        }

                        //----
                        // Gets the skeletal system
                        //----
                        ActorModel* actorModel = player.GetActorModel();
                        if (actorModel != null && gameMode == CameraModes.FirstPerson)
                        {
                            Skeleton* skeleton = actorModel->skeleton;
                            if (skeleton != null)
                            {
                                //----
                                // Loops though the skeletal parts and gets the pose layouts
                                //----
                                for (ushort p = 0; p < skeleton->PartialSkeletonCount; p++)
                                {
                                    hkaPose* playerPose = skeleton->PartialSkeletons[p].GetHavokPose(0);
                                    if (playerPose == null) continue;

                                    if (!boneNames.ContainsKey(*playerPose))
                                        boneNames.Add(*playerPose, new Dictionary<string, int>());

                                    if (!boneLayout.ContainsKey((UInt64)playerPose))
                                        boneLayout.Add((UInt64)playerPose, new List<KeyValuePair<Vector3, hkQsTransformf>>());

                                    //----
                                    // Loops though the pose bones and updates the ones that have tracking
                                    //----
                                    for (int i = 0; i < playerPose->LocalPose.Length; i++)
                                    {
                                        Vector3 overrideType = new Vector3(0, 0, 0);
                                        string boneName = playerPose->Skeleton->Bones[i].Name.String;
                                        if (!boneNames[*playerPose].ContainsKey(boneName))
                                            boneNames[*playerPose].Add(boneName, i);

                                        hkQsTransformf transform = playerPose->LocalPose[i];
                                        if (boneName == "j_ude_b_l" || boneName == "j_ude_b_r" || boneName == "n_hte_l" || boneName == "n_hte_r") // head forearm L/R wrist L/R
                                        {
                                            overrideType.Z = 1;
                                            transform.Scale.X = 0;
                                            transform.Scale.Y = 0;
                                            transform.Scale.Z = 0;
                                            transform.Scale.W = 0;
                                        }
                                        if (boneName == "j_sebo_a") // Spine A.B.C
                                        {
                                            //overrideType.Y = 1;
                                            Quaternion quat = Quaternion.Identity;
                                            transform.Rotation.X = quat.X;
                                            transform.Rotation.Y = quat.Y;
                                            transform.Rotation.Z = quat.Z;
                                            transform.Rotation.W = quat.W;
                                        }
                                        if (boneName == "j_f_eye_l" || boneName == "j_f_eye_r") // eyes
                                        {
                                        }
                                        if (xivr.cfg.data.motioncontrol)
                                        {
                                            float bodyHeight = firstPersonCameraHeight - actorModel->basePosition.Translation.Y;

                                            if (boneName == "j_te_l" || boneName == "n_hte_l") // left hand/wrist/weapon
                                            {
                                                if (playerPose->Skeleton->ParentIndices[i] != 0)
                                                    BoneParentOverrideList[boneName] = new KeyValuePair<int, short>(i, playerPose->Skeleton->ParentIndices[i]);
                                                playerPose->Skeleton->ParentIndices[i] = 0;

                                                overrideType.X = 1;
                                                Vector4 pos = new Vector4(lhcMatrixI.M41, bodyHeight + lhcMatrixI.M42, lhcMatrixI.M43, 0);
                                                transform.Translation.X = pos.X;
                                                transform.Translation.Y = pos.Y;
                                                transform.Translation.Z = pos.Z;

                                                overrideType.Y = 1;
                                                Quaternion quat = Quaternion.CreateFromRotationMatrix(lhcMatrixI);
                                                transform.Rotation.X = quat.X;
                                                transform.Rotation.Y = quat.Y;
                                                transform.Rotation.Z = quat.Z;
                                                transform.Rotation.W = quat.W;
                                            }
                                            else if (boneName == "j_te_r" || boneName == "n_hte_r") // right hand/wrist/weapon
                                            {
                                                if (playerPose->Skeleton->ParentIndices[i] != 0)
                                                    BoneParentOverrideList[boneName] = new KeyValuePair<int, short>(i, playerPose->Skeleton->ParentIndices[i]);
                                                playerPose->Skeleton->ParentIndices[i] = 0;

                                                overrideType.X = 1;
                                                Vector4 pos = new Vector4(rhcMatrixI.M41, bodyHeight + rhcMatrixI.M42, rhcMatrixI.M43, 0);
                                                transform.Translation.X = pos.X;
                                                transform.Translation.Y = pos.Y;
                                                transform.Translation.Z = pos.Z;

                                                overrideType.Y = 1;
                                                Quaternion quat = Quaternion.CreateFromRotationMatrix(rhcMatrixI);
                                                transform.Rotation.X = quat.X;
                                                transform.Rotation.Y = quat.Y;
                                                transform.Rotation.Z = quat.Z;
                                                transform.Rotation.W = quat.W;
                                            }
                                        }

                                        boneLayout[(UInt64)playerPose].Add(new KeyValuePair<Vector3, hkQsTransformf>(overrideType, transform));
                                    }
                                }
                            }
                        }
                    }
                }

                if (gameModeChanged == true && gameMode == CameraModes.ThirdPerson)
                    FirstToThirdPersonView();


                //----
                // Saves the target arrow alpha
                //----
                if (targetAddonAlpha == 0)
                {
                    AtkUnitBase* targetAddon = (AtkUnitBase*)DalamudApi.GameGui.GetAddonByName("_TargetCursor", 1);
                    if (targetAddon != null)
                        targetAddonAlpha = targetAddon->Alpha;
                }

                AtkUnitBase* CharSelectAddon = (AtkUnitBase*)DalamudApi.GameGui.GetAddonByName("_CharaSelectTitle", 1);
                AtkUnitBase* CharMakeAddon = (AtkUnitBase*)DalamudApi.GameGui.GetAddonByName("_CharaMakeTitle", 1);

                if (CharSelectAddon == null && CharMakeAddon == null && DalamudApi.ClientState.LocalPlayer == null)
                    timer = 100;
                
                if(timer > 0)
                {
                    forceFloatingScreen = true;
                    timer--;
                }

                curEye = nextEye[curEye];
                //SetFramePose();
            }
        }



        public void ForceFloatingScreen(bool forceFloating)
        {
            forceFloatingScreen = forceFloating;
        }

        public void SetRotateAmount(float x, float y)
        {
            rotateAmount.X = (x * RadianConversion);
            rotateAmount.Y = (y * RadianConversion);
        }

        public Point GetWindowSize()
        {
            Device *dev = Device.Instance();
            return new Point((int)dev->Width, (int)dev->Height);
        }

        public void WindowResize(IntPtr hwnd, int width, int height)
        {
            //----
            // Resizes the internal buffers
            //----
            Device* dev = Device.Instance();
            dev->NewWidth = (uint)width;
            dev->NewHeight = (uint)height;
            dev->RequestResolutionChange = 1;

            //----
            // Resizes the client window to match the internal buffers
            //----
            Imports.ResizeWindow(hwnd, width, height);
        }

        public void Dispose()
        {
            if (xivr.cfg.data.vLog)
                PluginLog.Log($"Dispose A {initalized} {hooksSet}");
            getThreadedDataHandle.Free();
            initalized = false;
            if (xivr.cfg.data.vLog)
                PluginLog.Log($"Dispose B {initalized} {hooksSet}");
        }

        private void AddClearCommand()
        {
            UInt64 threadedOffset = GetThreadedOffset();
            if (threadedOffset != 0)
            {
                UInt64 queueData = AllocateQueueMemmoryFn!(threadedOffset, 0x38);
                if (queueData != 0)
                {
                    stRenderQueueCommandClear* cmd = (stRenderQueueCommandClear*)queueData;
                    cmd->Clear();
                    cmd->clearType = 1;
                    cmd->colorR = 0;
                    cmd->colorG = 0;
                    cmd->colorB = 0;
                    cmd->colorA = 0;
                    cmd->unkn1 = 1;
                    PushbackFn!((threadedOffset + 0x18), (UInt64)(*(int*)(threadedOffset + 0x8)), queueData);
                }
            }
        }


        //----
        // GetThreadedData
        //----
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate UInt64 GetThreadedDataDg();
        GetThreadedDataDg GetThreadedDataFn;

        public void GetThreadedDataInit()
        {
            //----
            // Used to access gs:[00000058] until i can do it in c#
            //----
            getThreadedDataHandle = GCHandle.Alloc(GetThreadedDataASM, GCHandleType.Pinned);
            if (!Imports.VirtualProtectEx(Process.GetCurrentProcess().Handle, getThreadedDataHandle.AddrOfPinnedObject(), (UIntPtr)GetThreadedDataASM.Length, 0x40 /* EXECUTE_READWRITE */, out uint _))
                return;
            else
                if (!Imports.FlushInstructionCache(Process.GetCurrentProcess().Handle, getThreadedDataHandle.AddrOfPinnedObject(), (UIntPtr)GetThreadedDataASM.Length))
                return;

            GetThreadedDataFn = Marshal.GetDelegateForFunctionPointer<GetThreadedDataDg>(getThreadedDataHandle.AddrOfPinnedObject());
        }

        private UInt64 GetThreadedOffset()
        {
            UInt64 threadedData = GetThreadedDataFn();
            if (threadedData != 0)
            {
                threadedData = *(UInt64*)(threadedData + (UInt64)((*(int*)tls_index) * 8));
                threadedData = *(UInt64*)(threadedData + 0x250);
            }
            return threadedData;
        }

        //----
        // SetRenderTarget
        //----
        private delegate void SetRenderTargetDg(UInt64 a, UInt64 b, Structures.Texture** c, UInt64 d, UInt64 e, UInt64 f);
        [Signature(Signatures.SetRenderTarget, Fallibility = Fallibility.Fallible)]
        private SetRenderTargetDg? SetRenderTargetFn = null;

        //----
        // AllocateQueueMemory
        //----
        private delegate UInt64 AllocateQueueMemoryDg(UInt64 a, UInt64 b);
        [Signature(Signatures.AllocateQueueMemory, Fallibility = Fallibility.Fallible)]
        private AllocateQueueMemoryDg? AllocateQueueMemmoryFn = null;

        //----
        // Pushback
        //----
        private delegate void PushbackDg(UInt64 a, UInt64 b, UInt64 c);
        [Signature(Signatures.Pushback, Fallibility = Fallibility.Fallible)]
        private PushbackDg? PushbackFn = null;

        //----
        // PushbackUI
        //----
        private delegate void PushbackUIDg(UInt64 a, UInt64 b);
        [Signature(Signatures.PushbackUI, DetourName = nameof(PushbackUIFn))]
        private Hook<PushbackUIDg>? PushbackUIHook = null;

        [HandleStatus("PushbackUI")]
        public void PushbackUIStatus(bool status)
        {
            if (status == true)
                PushbackUIHook?.Enable();
            else
                PushbackUIHook?.Disable();
        }
        private void PushbackUIFn(UInt64 a, UInt64 b)
        {
            Structures.Texture* texture = Imports.GetUIRenderTexture(curEye);
            UInt64 threadedOffset = GetThreadedOffset();
            SetRenderTargetFn!(threadedOffset, 1, &texture, 0, 0, 0);

            AddClearCommand();

            overrideFromParent.Push(true);
            PushbackUIHook!.Original(a, b);
            overrideFromParent.Pop();
        }




        //----
        // DisableLeftClick
        //----
        private delegate void DisableLeftClickDg(void** a, byte* b, bool c);
        [Signature(Signatures.DisableLeftClick, DetourName = nameof(DisableLeftClickFn))]
        private readonly Hook<DisableLeftClickDg>? DisableLeftClickHook = null;

        [HandleStatus("DisableLeftClick")]
        public void DisableLeftClickStatus(bool status)
        {
            if (status == true)
                DisableLeftClickHook?.Enable();
            else
                DisableLeftClickHook?.Disable();
        }

        private void DisableLeftClickFn(void** a, byte* b, bool c)
        {
            if (b != null && b == a[16]) DisableLeftClickHook!.Original(a, b, c);
        }




        //----
        // DisableRightClick
        //----
        private delegate void DisableRightClickDg(void** a, byte* b, bool c);
        [Signature(Signatures.DisableRightClick, DetourName = nameof(DisableRightClickFn))]
        private Hook<DisableRightClickDg>? DisableRightClickHook = null;

        [HandleStatus("DisableRightClick")]
        public void DisableRightClickStatus(bool status)
        {
            if (status == true)
                DisableRightClickHook?.Enable();
            else
                DisableRightClickHook?.Disable();
        }

        private void DisableRightClickFn(void** a, byte* b, bool c)
        {
            if (b != null && b == a[16]) DisableRightClickHook!.Original(a, b, c);
        }




        //----
        // AtkUnitBase OnRequestedUpdate
        //----
        private delegate void OnRequestedUpdateDg(UInt64 a, UInt64 b, UInt64 c);
        [Signature(Signatures.OnRequestedUpdate, DetourName = nameof(OnRequestedUpdateFn))]
        private Hook<OnRequestedUpdateDg>? OnRequestedUpdateHook { get; set; } = null;

        [HandleStatus("OnRequestedUpdate")]
        public void OnRequestedUpdateStatus(bool status)
        {
            if (status == true)
                OnRequestedUpdateHook?.Enable();
            else
                OnRequestedUpdateHook?.Disable();
        }

        void OnRequestedUpdateFn(UInt64 a, UInt64 b, UInt64 c)
        {
            float globalScale = *(float*)globalScaleAddress;
            *(float*)globalScaleAddress = 1;
            OnRequestedUpdateHook!.Original(a, b, c);
            *(float*)globalScaleAddress = globalScale;
        }




        //----
        // DXGIPresent
        //----
        private delegate void DXGIPresentDg(UInt64 a, UInt64 b);
        [Signature(Signatures.DXGIPresent, DetourName = nameof(DXGIPresentFn))]
        private Hook<DXGIPresentDg>? DXGIPresentHook = null;

        [HandleStatus("DXGIPresent")]
        public void DXGIPresentStatus(bool status)
        {
            if (status == true)
                DXGIPresentHook?.Enable();
            else
                DXGIPresentHook?.Disable();
        }

        private void DXGIPresentFn(UInt64 a, UInt64 b)
        {
            if (forceFloatingScreen)
            {
                Imports.RenderUI(false, false);
                DXGIPresentHook!.Original(a, b);
                Imports.RenderFloatingScreen();
                Imports.RenderVR();
            }
            else
            {
                Imports.RenderUI(enableVR, enableFloatingHUD);
                DXGIPresentHook!.Original(a, b);
                Imports.SetTexture();
                Imports.RenderVR();
            }
        }



        //----
        // CameraManager Setup??
        //----
        private delegate void CamManagerSetMatrixDg(SceneCameraManager* camMngrInstance);
        [Signature(Signatures.CamManagerSetMatrix, DetourName = nameof(CamManagerSetMatrixFn))]
        private Hook<CamManagerSetMatrixDg>? CamManagerSetMatrixHook = null;

        [HandleStatus("CamManagerSetMatrix")]
        public void CamManagerSetMatrixStatus(bool status)
        {
            if (status == true)
                CamManagerSetMatrixHook?.Enable();
            else
                CamManagerSetMatrixHook?.Disable();
        }

        private void CamManagerSetMatrixFn(SceneCameraManager* camMngrInstance)
        {
            overrideFromParent.Push(true);
            CamManagerSetMatrixHook!.Original(camMngrInstance);
            overrideFromParent.Pop();
        }



        //----
        // CascadeShadow_UpdateConstantBuffer
        //----
        private delegate void CSUpdateConstBufDg(UInt64 a, UInt64 b);
        [Signature(Signatures.CSUpdateConstBuf, DetourName = nameof(CSUpdateConstBufFn))]
        private Hook<CSUpdateConstBufDg>? CSUpdateConstBufHook = null;

        [HandleStatus("CSUpdateConstBuf")]
        public void CSUpdateConstBufStatus(bool status)
        {
            if (status == true)
                CSUpdateConstBufHook?.Enable();
            else
                CSUpdateConstBufHook?.Disable();
        }

        private void CSUpdateConstBufFn(UInt64 a, UInt64 b)
        {
            overrideFromParent.Push(true);
            CSUpdateConstBufHook!.Original(a, b);
            overrideFromParent.Pop();
        }



        //----
        // SetUIProj
        //----
        private delegate void SetUIProjDg(UInt64 a, UInt64 b);
        [Signature(Signatures.SetUIProj, DetourName = nameof(SetUIProjFn))]
        private Hook<SetUIProjDg>? SetUIProjHook = null;

        [HandleStatus("SetUIProj")]
        public void SetUIProjStatus(bool status)
        {
            if (status == true)
                SetUIProjHook?.Enable();
            else
                SetUIProjHook?.Disable();
        }

        private void SetUIProjFn(UInt64 a, UInt64 b)
        {
            bool overrideFn = (overrideFromParent.Count == 0) ? false : overrideFromParent.Peek();
            if (overrideFn)
            {
                Structures.Texture* texture = Imports.GetUIRenderTexture(curEye);
                UInt64 threadedOffset = GetThreadedOffset();
                SetRenderTargetFn!(threadedOffset, 1, &texture, 0, 0, 0);
            }

            SetUIProjHook!.Original(a, b);
        }

        //----
        // Camera CalculateViewMatrix
        //----
        private delegate void CalculateViewMatrixDg(RawGameCamera* a);
        [Signature(Signatures.CalculateViewMatrix, DetourName = nameof(CalculateViewMatrixFn))]
        private Hook<CalculateViewMatrixDg>? CalculateViewMatrixHook = null;

        [HandleStatus("CalculateViewMatrix")]
        public void CalculateViewMatrixStatus(bool status)
        {
            if (status == true)
                CalculateViewMatrixHook?.Enable();
            else
                CalculateViewMatrixHook?.Disable();
        }

        //----
        // This function is also called for ui character stuff so only
        // act on it the first time its run per frame
        //----
        private void CalculateViewMatrixFn(RawGameCamera* rawGameCamera)
        {
            //----
            // Restore the camera to its prooper spot if disabled for collisions in first person
            //----
            if (csCameraManager->ActiveCameraIndex == 0 && gameMode == CameraModes.FirstPerson)
            {
                rawGameCamera->Y += 10.0f;
                rawGameCamera->LookAtY += 10.0f;
            }

            firstPersonCameraHeight = rawGameCamera->Y;
            rawGameCamera->ViewMatrix = Matrix4x4.Identity;
            CalculateViewMatrixHook!.Original(rawGameCamera);

            if (csCameraManager->ActiveCameraIndex == 0 && gameMode == CameraModes.FirstPerson)
            {
                rawGameCamera->Y -= 10.0f;
                rawGameCamera->LookAtY -= 10.0f;
            }

            if (frfCalculateViewMatrix == false)
            {
                frfCalculateViewMatrix = true;
                if (enableVR && enableFloatingHUD && forceFloatingScreen == false)
                {
                    Matrix4x4 horizonLockMatrix = Matrix4x4.Identity;
                    if (xivr.cfg.data.horizonLock || gameMode == CameraModes.FirstPerson)
                        horizonLockMatrix = Matrix4x4.CreateFromAxisAngle(new Vector3(1, 0, 0), rawGameCamera->CurrentVRotation);
                    horizonLockMatrix.M41 = (-xivr.cfg.data.offsetAmountX / 100);
                    horizonLockMatrix.M42 = (xivr.cfg.data.offsetAmountY / 100);
                    horizonLockMatrix.M43 = (xivr.cfg.data.offsetAmountZ / 100);

                    Matrix4x4 invGameViewMatrixAddr;
                    Vector3 angles = new Vector3();
                    if (xivr.cfg.data.conloc)
                    {
                        angles = GetAngles(lhcMatrix);
                    }
                    else if (xivr.cfg.data.hmdloc)
                    {
                        angles = GetAngles(hmdMatrix);
                        angles.Y *= -1;
                    }

                    Matrix4x4 revOnward = Matrix4x4.CreateFromAxisAngle(new Vector3(0, 1, 0), -angles.Y);
                    Matrix4x4 zoom = Matrix4x4.CreateTranslation(0, 0, -cameraZoom);
                    //revOnward = revOnward * zoom;
                    //Matrix4x4.Invert(revOnward, out revOnward);

                    if ((xivr.cfg.data.conloc == false && xivr.cfg.data.hmdloc == false) || gameMode == CameraModes.ThirdPerson)
                        revOnward = Matrix4x4.Identity;

                    if (xivr.cfg.data.swapEyes)
                        hmdMatrix = hmdMatrix * eyeOffsetMatrix[swapEyes[curEye]];
                    else
                        hmdMatrix = hmdMatrix * eyeOffsetMatrix[curEye];

                    rawGameCamera->ViewMatrix = rawGameCamera->ViewMatrix * horizonLockMatrix * revOnward * hmdMatrix;
                }
            }


            
        }


        //----
        // GetCameraPosition
        //----
        private delegate void GetCameraPositioDg(GameCamera* gameCamera, IntPtr target, float* vectorPosition, bool swapPerson);
        private Hook<GetCameraPositioDg>? GetCameraPositionHook = null;

        [HandleStatus("GetCameraPosition")]
        public void GetCameraPositionStatus(bool status)
        {
            if (status == true)
            {
                if (GetCameraPositionHook == null)
                    GetCameraPositionHook = Hook<GetCameraPositioDg>.FromAddress((IntPtr)csCameraManager->GameCamera->CameraBase.vtbl[15], GetCameraPositionFn);
                GetCameraPositionHook?.Enable();
            }
            else
                GetCameraPositionHook?.Disable();
        }

        private void GetCameraPositionFn(GameCamera* gameCamera, IntPtr target, float* vectorPosition, bool swapPerson)
        {
            GetCameraPositionHook!.Original(gameCamera, target, vectorPosition, swapPerson);

            //----
            // Hide the camera underground to disable collisions in first person
            //----
            if (csCameraManager->ActiveCameraIndex == 0 && gameMode == CameraModes.FirstPerson)
            {
                vectorPosition[1] -= 10.0f;
            }
        }


        //----
        // Camera UpdateRotation
        //----
        private delegate void UpdateRotationDg(GameCamera* gameCamera);
        [Signature(Signatures.UpdateRotation, DetourName = nameof(UpdateRotationFn))]
        private Hook<UpdateRotationDg>? UpdateRotationHook = null;

        [HandleStatus("UpdateRotation")]
        public void UpdateRotationStatus(bool status)
        {
            if (status == true)
                UpdateRotationHook?.Enable();
            else
                UpdateRotationHook?.Disable();
        }

        private void UpdateRotationFn(GameCamera* gameCamera)
        {
            if (forceFloatingScreen == false)
            {
                gameMode = gameCamera->Camera.Mode;
                Vector3 angles = new Vector3();

                if (xivr.cfg.data.conloc)
                {
                    angles = GetAngles(lhcMatrix);
                    angles.Y *= -1;
                }
                else if (xivr.cfg.data.hmdloc)
                {
                    angles = GetAngles(hmdMatrix);
                    angles.X *= -1;
                }

                onwardDiff = angles - onwardAngle;
                onwardAngle = angles;

                if (xivr.cfg.data.horizontalLock)
                    gameCamera->Camera.HRotationThisFrame2 = 0;
                if (xivr.cfg.data.verticalLock)
                    gameCamera->Camera.VRotationThisFrame2 = 0;
                if ((xivr.cfg.data.conloc == false && xivr.cfg.data.hmdloc == false) || gameMode == CameraModes.ThirdPerson)
                {
                    onwardDiff.Y = 0;
                    onwardDiff.X = 0;
                    onwardDiff.Z = 0;
                }

                float curH = gameCamera->Camera.CurrentHRotation;
                float curV = gameCamera->Camera.CurrentVRotation;
                //gameCamera->Camera.HRotationThisFrame1 += onwardDiff.Y + rotateAmount.X;
                gameCamera->Camera.HRotationThisFrame2 += onwardDiff.Y + rotateAmount.X;
                //gameCamera->Camera.VRotationThisFrame1 += onwardDiff.X + rotateAmount.Y;
                //gameCamera->Camera.VRotationThisFrame2 += onwardDiff.X + rotateAmount.Y;

                if(xivr.cfg.data.vertloc)
                    gameCamera->Camera.VRotationThisFrame2 += onwardDiff.X + rotateAmount.Y;
                else
                    gameCamera->Camera.VRotationThisFrame2 += rotateAmount.Y;

                rotateAmount.X = 0;
                rotateAmount.Y = 0;

                cameraZoom = gameCamera->Camera.CurrentZoom;
                UpdateRotationHook!.Original(gameCamera);
            }
            else
            {
                UpdateRotationHook!.Original(gameCamera);
            }
        }



        //----
        // MakeProjectionMatrix2
        //----
        private delegate Matrix4x4 MakeProjectionMatrix2Dg(Matrix4x4 projMatrix, float b, float c, float d, float e);
        [Signature(Signatures.MakeProjectionMatrix2, DetourName = nameof(MakeProjectionMatrix2Fn))]
        private Hook<MakeProjectionMatrix2Dg>? MakeProjectionMatrix2Hook = null;

        [HandleStatus("MakeProjectionMatrix2")]
        public void MakeProjectionMatrix2Status(bool status)
        {
            if (status == true)
                MakeProjectionMatrix2Hook?.Enable();
            else
                MakeProjectionMatrix2Hook?.Disable();
        }

        private Matrix4x4 MakeProjectionMatrix2Fn(Matrix4x4 projMatrix, float b, float c, float d, float e)
        {
            bool overrideMatrix = (overrideFromParent.Count == 0) ? false : overrideFromParent.Peek();
            Matrix4x4 retVal = MakeProjectionMatrix2Hook!.Original(projMatrix, b, c, d, e);
            if (enableVR && enableFloatingHUD && overrideMatrix && forceFloatingScreen == false)
            {
                if (xivr.cfg.data.swapEyes)
                {
                    gameProjectionMatrix[swapEyes[curEye]].M43 = retVal.M43;
                    retVal = gameProjectionMatrix[swapEyes[curEye]];
                }
                else
                {
                    gameProjectionMatrix[curEye].M43 = retVal.M43;
                    retVal = gameProjectionMatrix[curEye];
                }
            }
            return retVal;
        }



        //----
        // CascadeShadow MakeProjectionMatrix
        //----
        private delegate Matrix4x4 CSMakeProjectionMatrixDg(Matrix4x4 projMatrix, float b, float c, float d, float e);
        [Signature(Signatures.CSMakeProjectionMatrix, DetourName = nameof(CSMakeProjectionMatrixFn))]
        private Hook<CSMakeProjectionMatrixDg>? CSMakeProjectionMatrixHook = null;

        [HandleStatus("CSMakeProjectionMatrix")]
        public void CSMakeProjectionMatrixStatus(bool status)
        {
            if (status == true)
                CSMakeProjectionMatrixHook?.Enable();
            else
                CSMakeProjectionMatrixHook?.Disable();
        }

        private Matrix4x4 CSMakeProjectionMatrixFn(Matrix4x4 projMatrix, float b, float c, float d, float e)
        {
            bool overrideMatrix = (overrideFromParent.Count == 0) ? false : overrideFromParent.Peek();
            if (enableVR && enableFloatingHUD && overrideMatrix && forceFloatingScreen == false)
            {
                b = 2.0f;
            }
            Matrix4x4 retVal = CSMakeProjectionMatrixHook!.Original(projMatrix, b, c, d, e);
            return retVal;
        }



        //----
        // RenderThreadSetRenderTarget
        //----
        private delegate void RenderThreadSetRenderTargetDg(UInt64 a, UInt64 b);
        [Signature(Signatures.RenderThreadSetRenderTarget, DetourName = nameof(RenderThreadSetRenderTargetFn))]
        private Hook<RenderThreadSetRenderTargetDg>? RenderThreadSetRenderTargetHook = null;

        [HandleStatus("RenderThreadSetRenderTarget")]
        public void RenderThreadSetRenderTargetStatus(bool status)
        {
            if (status == true)
                RenderThreadSetRenderTargetHook?.Enable();
            else
                RenderThreadSetRenderTargetHook?.Disable();
        }

        private void RenderThreadSetRenderTargetFn(UInt64 a, UInt64 b)
        {
            if ((b + 0x8) != 0)
            {
                Structures.Texture* rendTrg = *(Structures.Texture**)(b + 0x8);
                if (rendTrg->uk5 == 0x990F0F0)
                    Imports.SetThreadedEye(0);
                else if (rendTrg->uk5 == 0x990F0F0F)
                    Imports.SetThreadedEye(1);
            }
            RenderThreadSetRenderTargetHook!.Original(a, b);
        }




        //----
        // NamePlateDraw
        //----
        private delegate void NamePlateDrawDg(AddonNamePlate* a);
        [Signature(Signatures.NamePlateDraw, DetourName = nameof(NamePlateDrawFn))]
        private Hook<NamePlateDrawDg>? NamePlateDrawHook = null;

        [HandleStatus("NamePlateDraw")]
        public void NamePlateDrawStatus(bool status)
        {
            if (status == true)
                NamePlateDrawHook?.Enable();
            else
                NamePlateDrawHook?.Disable();
        }

        private void NamePlateDrawFn(AddonNamePlate* a)
        {
            if (enableVR)
            {
                //----
                // Disables the target arrow until it can be put in the world
                //----
                AtkUnitBase* targetAddon = (AtkUnitBase*)DalamudApi.GameGui.GetAddonByName("_TargetCursor", 1);
                if (targetAddon != null)
                {
                    targetAddon->Alpha = 1;
                    targetAddon->Hide(true);
                    //targetAddon->RootNode->SetUseDepthBasedPriority(true);
                }

                SetupVRTargetCursor();

                for (byte i = 0; i < NamePlateCount; i++)
                {
                    NamePlateObject* npObj = &a->NamePlateObjectArray[i];
                    AtkComponentBase* npComponent = npObj->RootNode->Component;

                    for (int j = 0; j < npComponent->UldManager.NodeListCount; j++)
                    {
                        AtkResNode* child = npComponent->UldManager.NodeList[j];
                        child->SetUseDepthBasedPriority(true);
                    }

                    npObj->RootNode->Component->UldManager.UpdateDrawNodeList();
                }

                NamePlateObject* selectedNamePlate = null;
                var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
                var ui3DModule = framework->GetUiModule()->GetUI3DModule();

                for (int i = 0; i < ui3DModule->NamePlateObjectInfoCount; i++)
                {
                    var objectInfo = ((UI3DModule.ObjectInfo**)ui3DModule->NamePlateObjectInfoPointerArray)[i];

                    TargetSystem* targSys = (TargetSystem*)DalamudApi.TargetManager.Address;
                    if (objectInfo->GameObject == targSys->Target)
                    {                        
                        selectedNamePlate = &a->NamePlateObjectArray[objectInfo->NamePlateIndex];
                        break;
                    }
                }

                UpdateVRCursorSize();
                SetVRCursor(selectedNamePlate);
                
            }

            NamePlateDrawHook!.Original(a);
        }



        //----
        // RunBoneMath
        //----
        private delegate UInt64 RunBoneMathDg(hkaPose *a, int b);
        [Signature(Signatures.RunBoneMath, DetourName = nameof(RunBoneMathFn))]
        private Hook<RunBoneMathDg>? RunBoneMathHook = null;

        [HandleStatus("RunBoneMath")]
        public void RunBoneMathStatus(bool status)
        {
            if (status == true)
                RunBoneMathHook?.Enable();
            else
                RunBoneMathHook?.Disable();
        }

        private UInt64 RunBoneMathFn(hkaPose *pose, int b)
        {
            UInt64 retVal = RunBoneMathHook!.Original(pose, b);

            PlayerCharacter? player = DalamudApi.ClientState.LocalPlayer;
            if (player == null)
                return retVal;

            ActorModel* actorModel = player.GetActorModel();
            if (actorModel == null)
                return retVal;

            Skeleton* skeleton = actorModel->skeleton;
            if(skeleton == null)
                return retVal;

            if (boneLayout.Count == 0)
                return retVal;

            for (ushort p = 0; p < skeleton->PartialSkeletonCount; p++)
            {
                hkaPose* playerPose = skeleton->PartialSkeletons[p].GetHavokPose(0);
                if (playerPose == null) continue;

                if (pose == playerPose)
                {
                    for (int i = 0; i < pose->LocalPose.Length; i++)
                    {
                        hkQsTransformf newTransform = pose->LocalPose[i];

                        if (boneLayout[(UInt64)playerPose][i].Key.X == 1) 
                            newTransform.Translation = boneLayout[(UInt64)playerPose][i].Value.Translation;
                        if (boneLayout[(UInt64)playerPose][i].Key.Y == 1)
                            newTransform.Rotation = boneLayout[(UInt64)playerPose][i].Value.Rotation;
                        if (boneLayout[(UInt64)playerPose][i].Key.Z == 1)
                            newTransform.Scale = boneLayout[(UInt64)playerPose][i].Value.Scale;
                        
                        pose->LocalPose[i] = newTransform;
                    }
                }
            }

            return retVal;
        }


        /*
        //----
        // LoadCharacter
        //----
        private delegate UInt64 LoadCharacterDg(UInt64 a, UInt64 b, UInt64 c, UInt64 d, UInt64 e, UInt64 f);
        [Signature(Signatures.LoadCharacter, DetourName = nameof(LoadCharacterFn))]
        private Hook<LoadCharacterDg>? LoadCharacterHook = null;

        [HandleStatus("LoadCharacter")]
        public void LoadCharacterStatus(bool status)
        {
            if (status == true)
                LoadCharacterHook?.Enable();
            else
                LoadCharacterHook?.Disable();
        }

        private UInt64 LoadCharacterFn(UInt64 a, UInt64 b, UInt64 c, UInt64 d, UInt64 e, UInt64 f)
        {
            PlayerCharacter? player = DalamudApi.ClientState.LocalPlayer;
            if (player != null && (UInt64)player.Address == a)
            {
                CharCustData* cData = (CharCustData*)c;
                CharEquipData* eData = (CharEquipData*)d;
            }
            return LoadCharacterHook!.Original(a, b, c, d, e, f);
        }
        */

        //----
        // ChangeEquipment
        //----
        private delegate void ChangeEquipmentDg(UInt64 address, CharEquipSlots index, CharEquipSlotData item);
        [Signature(Signatures.ChangeEquipment, DetourName = nameof(ChangeEquipmentFn))]
        private Hook<ChangeEquipmentDg>? ChangeEquipmentHook = null;

        [HandleStatus("ChangeEquipment")]
        public void ChangeEquipmentStatus(bool status)
        {
            if (status == true)
                ChangeEquipmentHook?.Enable();
            else
                ChangeEquipmentHook?.Disable();
        }

        private void ChangeEquipmentFn(UInt64 address, CharEquipSlots index, CharEquipSlotData item)
        {
            PlayerCharacter? player = DalamudApi.ClientState.LocalPlayer;
            if (player != null)
            {
                Character* playerChar = (Character*)player.Address;
                if ((((UInt64)playerChar) + 0x6D0) == address)
                {
                    haveSavedEquipmentSet = true;
                    currentEquipmentSet.Data[(int)index] = item.Data;
                }
            }
            //PluginLog.Log($"ChangeEquipmentFn {address:X} {index} {item.Id}, {item.Variant}, {item.Dye}");
            ChangeEquipmentHook!.Original(address, index, item);
        }

        //----
        // ChangeWeapon
        //----
        private delegate void ChangeWeaponDg(UInt64 address, CharWeaponSlots index, CharWeaponSlotData item, byte d, byte e, byte f, byte g);
        [Signature(Signatures.ChangeWeapon, DetourName = nameof(ChangeWeaponFn))]
        private Hook<ChangeWeaponDg>? ChangeWeaponHook = null;

        [HandleStatus("ChangeWeapon")]
        public void ChangeWeaponStatus(bool status)
        {
            if (status == true)
                ChangeWeaponHook?.Enable();
            else
                ChangeWeaponHook?.Disable();
        }

        private void ChangeWeaponFn(UInt64 address, CharWeaponSlots index, CharWeaponSlotData item, byte d, byte e, byte f, byte g)
        {
            PlayerCharacter? player = DalamudApi.ClientState.LocalPlayer;
            if (player != null)
            {
                Character* playerChar = (Character*)player.Address;
                if ((((UInt64)playerChar) + 0x6D0) == address)
                {
                    haveSavedEquipmentSet = true;
                    currentWeaponSet.Data[(int)index] = item.Data;
                }
            }
            //PluginLog.Log($"ChangeWeaponFn {address:X} {index} | {item.Type}, {item.Id}, {item.Variant}, {item.Dye} | {d}, {e}, {f}, {g}");
            ChangeWeaponHook!.Original(address, index, item, d, e, f, g);
        }

        /*
        //----
        // EquipGearsetInternal
        //----
        private delegate void EquipGearsetInternalDg(UInt64 address, int b, byte c);
        [Signature(Signatures.EquipGearsetInternal, DetourName = nameof(EquipGearsetInternalFn))]
        private Hook<EquipGearsetInternalDg>? EquipGearsetInternalHook = null;

        [HandleStatus("EquipGearsetInternal")]
        public void EquipGearsetInternalStatus(bool status)
        {
            if (status == true)
                EquipGearsetInternalHook?.Enable();
            else
                EquipGearsetInternalHook?.Disable();
        }

        private void EquipGearsetInternalFn(UInt64 address, int b, byte c)
        {
            //PluginLog.Log($"EquipGearsetInternalFn {address:X} {b} {c}");
            EquipGearsetInternalHook!.Original(address, b, c);
        }
        */


        //----
        // Input.GetAnalogueValue
        //----
        private delegate Int32 GetAnalogueValueDg(UInt64 a, UInt64 b);
        [Signature(Signatures.GetAnalogueValue, DetourName = nameof(GetAnalogueValueFn))]
        private Hook<GetAnalogueValueDg>? GetAnalogueValueHook = null;

        [HandleStatus("GetAnalogueValue")]
        public void GetAnalogueValueStatus(bool status)
        {
            if (status == true)
                GetAnalogueValueHook?.Enable();
            else
                GetAnalogueValueHook?.Disable();
        }



        // 0 mouse left right
        // 1 mouse up down
        // 3 left | left right
        // 4 left | up down
        // 5 right | left right
        // 6 right | up down

        private Int32 GetAnalogueValueFn(UInt64 a, UInt64 b)
        {
            Int32 retVal = GetAnalogueValueHook!.Original(a, b);

            if (enableVR)
            {
                switch (b)
                {
                    case 0:
                    case 1:
                    case 2:
                        break;
                    case 3:
                        break;
                    case 4:
                        break;
                    case 5:
                        //PluginLog.Log($"GetAnalogueValueFn: {retVal}");
                        if (MathF.Abs(retVal) >= 0 && MathF.Abs(retVal) < 15) rightHorizontalCenter = true;
                        if (xivr.cfg.data.horizontalLock && MathF.Abs(leftBumperValue) < 0.5)
                        {
                            if (MathF.Abs(retVal) > 75 && rightHorizontalCenter)
                            {
                                rightHorizontalCenter = false;
                                rotateAmount.X -= (xivr.cfg.data.snapRotateAmountX * RadianConversion) * MathF.Sign(retVal);
                            }
                            retVal = 0;
                        }
                        break;
                    case 6:
                        //PluginLog.Log($"GetAnalogueValueFn: {retVal}");
                        if (MathF.Abs(retVal) >= 0 && MathF.Abs(retVal) < 15) rightVerticalCenter = true;
                        if (xivr.cfg.data.verticalLock && MathF.Abs(leftBumperValue) < 0.5)
                        {
                            if (MathF.Abs(retVal) > 75 && rightVerticalCenter)
                            {
                                rightVerticalCenter = false;
                                rotateAmount.Y -= (xivr.cfg.data.snapRotateAmountY * RadianConversion) * MathF.Sign(retVal);
                            }
                            retVal = 0;
                        }
                        break;
                }
            }
            return retVal;
        }



        //----
        // Controller Input
        //---- BaseAddress + 0x4E37F0
        private delegate void ControllerInputDg(UInt64 a, UInt64 b, uint c);
        [Signature(Signatures.ControllerInput, DetourName = nameof(ControllerInputFn))]
        private Hook<ControllerInputDg>? ControllerInputHook = null;

        [HandleStatus("ControllerInput")]
        public void ControllerInputStatus(bool status)
        {
            if (status == true)
                ControllerInputHook?.Enable();
            else
                ControllerInputHook?.Disable();
        }

        public void ControllerInputFn(UInt64 a, UInt64 b, uint c)
        {
            UInt64 controllerBase = *(UInt64*)(a + 0x70);
            UInt64 controllerIndex = *(byte*)(a + 0x434);

            UInt64 controllerAddress = controllerBase + 0x30 + ((controllerIndex * 0x1E6) * 4);
            XBoxButtonOffsets* offsets = (XBoxButtonOffsets*)((controllerIndex * 0x798) + controllerBase);

            if (xboxStatus.dpad_up.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->dpad_up * 4)) = xboxStatus.dpad_up.value;
            if (xboxStatus.dpad_down.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->dpad_down * 4)) = xboxStatus.dpad_down.value;
            if (xboxStatus.dpad_left.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->dpad_left * 4)) = xboxStatus.dpad_left.value;
            if (xboxStatus.dpad_right.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->dpad_right * 4)) = xboxStatus.dpad_right.value;
            if (xboxStatus.left_stick_down.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->left_stick_down * 4)) = xboxStatus.left_stick_down.value;
            if (xboxStatus.left_stick_up.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->left_stick_up * 4)) = xboxStatus.left_stick_up.value;
            if (xboxStatus.left_stick_left.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->left_stick_left * 4)) = xboxStatus.left_stick_left.value;
            if (xboxStatus.left_stick_right.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->left_stick_right * 4)) = xboxStatus.left_stick_right.value;
            if (xboxStatus.right_stick_down.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->right_stick_down * 4)) = xboxStatus.right_stick_down.value;
            if (xboxStatus.right_stick_up.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->right_stick_up * 4)) = xboxStatus.right_stick_up.value;
            if (xboxStatus.right_stick_left.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->right_stick_left * 4)) = xboxStatus.right_stick_left.value;
            if (xboxStatus.right_stick_right.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->right_stick_right * 4)) = xboxStatus.right_stick_right.value;
            if (xboxStatus.button_y.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->button_y * 4)) = xboxStatus.button_y.value;
            if (xboxStatus.button_b.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->button_b * 4)) = xboxStatus.button_b.value;
            if (xboxStatus.button_a.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->button_a * 4)) = xboxStatus.button_a.value;
            if (xboxStatus.button_x.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->button_x * 4)) = xboxStatus.button_x.value;
            if (xboxStatus.left_bumper.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->left_bumper * 4)) = xboxStatus.left_bumper.value;
            if (xboxStatus.left_trigger.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->left_trigger * 4)) = xboxStatus.left_trigger.value;
            if (xboxStatus.left_stick_click.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->left_stick_click * 4)) = xboxStatus.left_stick_click.value;
            if (xboxStatus.right_bumper.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->right_bumper * 4)) = xboxStatus.right_bumper.value;
            if (xboxStatus.right_trigger.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->right_trigger * 4)) = xboxStatus.right_trigger.value;
            if (xboxStatus.right_stick_click.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->right_stick_click * 4)) = xboxStatus.right_stick_click.value;
            if (xboxStatus.start.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->start * 4)) = xboxStatus.start.value;
            if (xboxStatus.select.active && xivr.cfg.data.motioncontrol)
                *(float*)(controllerAddress + (UInt64)(offsets->select * 4)) = xboxStatus.select.value;

            leftBumperValue = *(float*)(controllerAddress + (UInt64)(offsets->left_bumper * 4));
            ControllerInputHook!.Original(a, b, c);
        }





        public static Vector3 GetAngles(Matrix4x4 source)
        {
            float thetaX, thetaY, thetaZ = 0.0f;
            thetaX = MathF.Asin(source.M32);

            if (thetaX < (Math.PI / 2))
            {
                if (thetaX > (-Math.PI / 2))
                {
                    thetaZ = MathF.Atan2(-source.M12, source.M22);
                    thetaY = MathF.Atan2(-source.M31, source.M33);
                }
                else
                {
                    thetaZ = -MathF.Atan2(-source.M13, source.M11);
                    thetaY = 0;
                }
            }
            else
            {
                thetaZ = MathF.Atan2(source.M13, source.M11);
                thetaY = 0;
            }
            Vector3 angles = new Vector3(thetaX, thetaY, thetaZ);
            return angles;
        }






        [DllImport("user32.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall)]
        public static extern void keybd_event(uint bVk, uint bScan, uint dwFlags, uint dwExtraInfo);

        [DllImport("user32.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall)]
        public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint cButtons, uint dwExtraInfo);

        const int MOUSEEVENTF_LEFTDOWN = 0x02;
        const int MOUSEEVENTF_LEFTUP = 0x04;
        const int MOUSEEVENTF_RIGHTDOWN = 0x08;
        const int MOUSEEVENTF_RIGHTUP = 0x10;

        const int KEYEVENTF_KEYDOWN = 0x0000;
        const int KEYEVENTF_EXTENDEDKEY = 0x0001;
        const int KEYEVENTF_KEYUP = 0x0002;

        const int VK_SHIFT = 0xA0;
        const int VK_ALT = 0xA4;
        const int VK_CONTROL = 0xA2;
        const int VK_ESCAPE = 0x1B;

        const int VK_F1 = 0x70;
        const int VK_F2 = 0x71;
        const int VK_F3 = 0x72;
        const int VK_F4 = 0x73;
        const int VK_F5 = 0x74;
        const int VK_F6 = 0x75;
        const int VK_F7 = 0x76;
        const int VK_F8 = 0x77;
        const int VK_F9 = 0x78;
        const int VK_F10 = 0x79;
        const int VK_F11 = 0x7A;
        const int VK_F12 = 0x7B;

        public XBoxStatus xboxStatus = new XBoxStatus();
        bool rightHorizontalCenter = false;
        bool rightVerticalCenter = false;

        //----
        // Movement
        //----
        [HandleInputAttribute(ActionButtonLayout.movement)]
        public void inputMovement(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            xboxStatus.left_stick_left.Set();
            xboxStatus.left_stick_right.Set();
            xboxStatus.left_stick_up.Set();
            xboxStatus.left_stick_down.Set();

            if (analog.x < 0)
                xboxStatus.left_stick_left.Set(true, MathF.Abs(analog.x));
            else if (analog.x > 0)
                xboxStatus.left_stick_right.Set(true, MathF.Abs(analog.x));

            if (analog.y > 0)
                xboxStatus.left_stick_up.Set(true, MathF.Abs(analog.y));
            else if (analog.y < 0)
                xboxStatus.left_stick_down.Set(true, MathF.Abs(analog.y));
        }

        [HandleInputAttribute(ActionButtonLayout.rotation)]
        public void inputRotation(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            xboxStatus.right_stick_left.Set();
            xboxStatus.right_stick_right.Set();
            xboxStatus.right_stick_up.Set();
            xboxStatus.right_stick_down.Set();

            if (analog.x < 0)
                xboxStatus.right_stick_left.Set(true, MathF.Abs(analog.x));
            else if (analog.x > 0)
                xboxStatus.right_stick_right.Set(true, MathF.Abs(analog.x));

            if (analog.y > 0)
                xboxStatus.right_stick_up.Set(true, MathF.Abs(analog.y));
            else if (analog.y < 0)
                xboxStatus.right_stick_down.Set(true, MathF.Abs(analog.y));
        }

        //----
        // Mouse
        //----

        [HandleInputAttribute(ActionButtonLayout.leftClick)]
        public void inputLeftClick(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.leftClick] == false)
            {
                inputState[ActionButtonLayout.leftClick] = true;
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.leftClick] == true)
            {
                inputState[ActionButtonLayout.leftClick] = false;
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
            }
        }

        [HandleInputAttribute(ActionButtonLayout.rightClick)]
        public void inputRightClick(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.rightClick] == false)
            {
                inputState[ActionButtonLayout.rightClick] = true;
                mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.rightClick] == true)
            {
                inputState[ActionButtonLayout.rightClick] = false;
                mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0);
            }
        }


        //----
        // Keys
        //----

        [HandleInputAttribute(ActionButtonLayout.recenter)]
        public void inputRecenter(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.recenter] == false)
            {
                inputState[ActionButtonLayout.recenter] = true;
                Imports.Recenter();
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.recenter] == true)
            {
                inputState[ActionButtonLayout.recenter] = false;
            }
        }

        [HandleInputAttribute(ActionButtonLayout.shift)]
        public void inputShift(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.shift] == false)
            {
                inputState[ActionButtonLayout.shift] = true;
                keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYDOWN, 0);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.shift] == true)
            {
                inputState[ActionButtonLayout.shift] = false;
                keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, 0);
            }
        }

        [HandleInputAttribute(ActionButtonLayout.alt)]
        public void inputAlt(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.alt] == false)
            {
                inputState[ActionButtonLayout.alt] = true;
                keybd_event(VK_ALT, 0, KEYEVENTF_KEYDOWN, 0);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.alt] == true)
            {
                inputState[ActionButtonLayout.alt] = false;
                keybd_event(VK_ALT, 0, KEYEVENTF_KEYUP, 0);
            }
        }

        [HandleInputAttribute(ActionButtonLayout.control)]
        public void inputControl(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.control] == false)
            {
                inputState[ActionButtonLayout.control] = true;
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYDOWN, 0);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.control] == true)
            {
                inputState[ActionButtonLayout.control] = false;
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
            }
        }

        [HandleInputAttribute(ActionButtonLayout.escape)]
        public void inputEscape(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.escape] == false)
            {
                inputState[ActionButtonLayout.escape] = true;
                keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYDOWN, 0);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.escape] == true)
            {
                inputState[ActionButtonLayout.escape] = false;
                keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, 0);
            }
        }

        //----
        // F Keys
        //----

        [HandleInputAttribute(ActionButtonLayout.button01)]
        public void inputButton01(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.button01] == false)
            {
                inputState[ActionButtonLayout.button01] = true;
                keybd_event(VK_F1, 0, KEYEVENTF_KEYDOWN, 0);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.button01] == true)
            {
                inputState[ActionButtonLayout.button01] = false;
                keybd_event(VK_F1, 0, KEYEVENTF_KEYUP, 0);
            }
        }

        [HandleInputAttribute(ActionButtonLayout.button02)]
        public void inputButton02(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.button02] == false)
            {
                inputState[ActionButtonLayout.button02] = true;
                keybd_event(VK_F2, 0, KEYEVENTF_KEYDOWN, 0);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.button02] == true)
            {
                inputState[ActionButtonLayout.button02] = false;
                keybd_event(VK_F2, 0, KEYEVENTF_KEYUP, 0);
            }
        }

        [HandleInputAttribute(ActionButtonLayout.button03)]
        public void inputButton03(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.button03] == false)
            {
                inputState[ActionButtonLayout.button03] = true;
                keybd_event(VK_F3, 0, KEYEVENTF_KEYDOWN, 0);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.button03] == true)
            {
                inputState[ActionButtonLayout.button03] = false;
                keybd_event(VK_F3, 0, KEYEVENTF_KEYUP, 0);
            }
        }

        [HandleInputAttribute(ActionButtonLayout.button04)]
        public void inputButton04(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.button04] == false)
            {
                inputState[ActionButtonLayout.button04] = true;
                keybd_event(VK_F4, 0, KEYEVENTF_KEYDOWN, 0);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.button04] == true)
            {
                inputState[ActionButtonLayout.button04] = false;
                keybd_event(VK_F4, 0, KEYEVENTF_KEYUP, 0);
            }
        }

        [HandleInputAttribute(ActionButtonLayout.button05)]
        public void inputButton05(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.button05] == false)
            {
                inputState[ActionButtonLayout.button05] = true;
                keybd_event(VK_F5, 0, KEYEVENTF_KEYDOWN, 0);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.button05] == true)
            {
                inputState[ActionButtonLayout.button05] = false;
                keybd_event(VK_F5, 0, KEYEVENTF_KEYUP, 0);
            }
        }

        [HandleInputAttribute(ActionButtonLayout.button06)]
        public void inputButton06(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.button06] == false)
            {
                inputState[ActionButtonLayout.button06] = true;
                keybd_event(VK_F6, 0, KEYEVENTF_KEYDOWN, 0);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.button06] == true)
            {
                inputState[ActionButtonLayout.button06] = false;
                keybd_event(VK_F6, 0, KEYEVENTF_KEYUP, 0);
            }
        }

        [HandleInputAttribute(ActionButtonLayout.button07)]
        public void inputButton07(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.button07] == false)
            {
                inputState[ActionButtonLayout.button07] = true;
                keybd_event(VK_F7, 0, KEYEVENTF_KEYDOWN, 0);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.button07] == true)
            {
                inputState[ActionButtonLayout.button07] = false;
                keybd_event(VK_F7, 0, KEYEVENTF_KEYUP, 0);
            }
        }

        [HandleInputAttribute(ActionButtonLayout.button08)]
        public void inputButton08(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.button08] == false)
            {
                inputState[ActionButtonLayout.button08] = true;
                keybd_event(VK_F8, 0, KEYEVENTF_KEYDOWN, 0);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.button08] == true)
            {
                inputState[ActionButtonLayout.button08] = false;
                keybd_event(VK_F8, 0, KEYEVENTF_KEYUP, 0);
            }
        }

        [HandleInputAttribute(ActionButtonLayout.button09)]
        public void inputButton09(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.button09] == false)
            {
                inputState[ActionButtonLayout.button09] = true;
                keybd_event(VK_F9, 0, KEYEVENTF_KEYDOWN, 0);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.button09] == true)
            {
                inputState[ActionButtonLayout.button09] = false;
                keybd_event(VK_F9, 0, KEYEVENTF_KEYUP, 0);
            }
        }

        [HandleInputAttribute(ActionButtonLayout.button10)]
        public void inputButton10(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.button10] == false)
            {
                inputState[ActionButtonLayout.button10] = true;
                keybd_event(VK_F10, 0, KEYEVENTF_KEYDOWN, 0);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.button10] == true)
            {
                inputState[ActionButtonLayout.button10] = false;
                keybd_event(VK_F10, 0, KEYEVENTF_KEYUP, 0);
            }
        }

        [HandleInputAttribute(ActionButtonLayout.button11)]
        public void inputButton11(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.button11] == false)
            {
                inputState[ActionButtonLayout.button11] = true;
                keybd_event(VK_F11, 0, KEYEVENTF_KEYDOWN, 0);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.button11] == true)
            {
                inputState[ActionButtonLayout.button11] = false;
                keybd_event(VK_F11, 0, KEYEVENTF_KEYUP, 0);
            }
        }

        [HandleInputAttribute(ActionButtonLayout.button12)]
        public void inputButton12(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.button12] == false)
            {
                inputState[ActionButtonLayout.button12] = true;
                keybd_event(VK_F12, 0, KEYEVENTF_KEYDOWN, 0);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.button12] == true)
            {
                inputState[ActionButtonLayout.button12] = false;
                keybd_event(VK_F12, 0, KEYEVENTF_KEYUP, 0);
            }
        }


        //----
        // XBox Buttons
        //----

        [HandleInputAttribute(ActionButtonLayout.xbox_button_y)]
        public void inputXBoxButtonY(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.xbox_button_y] == false)
            {
                inputState[ActionButtonLayout.xbox_button_y] = true;
                xboxStatus.button_y.Set(true, 1.0f);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.xbox_button_y] == true)
            {
                inputState[ActionButtonLayout.xbox_button_y] = false;
                xboxStatus.button_y.Set();
            }
        }

        [HandleInputAttribute(ActionButtonLayout.xbox_button_x)]
        public void inputXBoxButtonX(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.xbox_button_x] == false)
            {
                inputState[ActionButtonLayout.xbox_button_x] = true;
                xboxStatus.button_x.Set(true, 1.0f);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.xbox_button_x] == true)
            {
                inputState[ActionButtonLayout.xbox_button_x] = false;
                xboxStatus.button_x.Set();
            }
        }

        [HandleInputAttribute(ActionButtonLayout.xbox_button_a)]
        public void inputXBoxButtonA(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.xbox_button_a] == false)
            {
                inputState[ActionButtonLayout.xbox_button_a] = true;
                xboxStatus.button_a.Set(true, 1.0f);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.xbox_button_a] == true)
            {
                inputState[ActionButtonLayout.xbox_button_a] = false;
                xboxStatus.button_a.Set();
            }
        }

        [HandleInputAttribute(ActionButtonLayout.xbox_button_b)]
        public void inputXBoxButtonB(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.xbox_button_b] == false)
            {
                inputState[ActionButtonLayout.xbox_button_b] = true;
                xboxStatus.button_b.Set(true, 1.0f);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.xbox_button_b] == true)
            {
                inputState[ActionButtonLayout.xbox_button_b] = false;
                xboxStatus.button_b.Set();
            }
        }

        [HandleInputAttribute(ActionButtonLayout.xbox_left_trigger)]
        public void inputXBoxLeftTrigger(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            xboxStatus.left_trigger.Set();
            if (analog.x > 0)
                xboxStatus.left_trigger.Set(true, analog.x);
        }

        [HandleInputAttribute(ActionButtonLayout.xbox_left_bumper)]
        public void inputXBoxLeftBumper(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            xboxStatus.left_bumper.Set();
            if (analog.x > 0)
                xboxStatus.left_bumper.Set(true, analog.x);
        }

        [HandleInputAttribute(ActionButtonLayout.xbox_left_stick_click)]
        public void inputXBoxLeftStickClick(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.xbox_left_stick_click] == false)
            {
                inputState[ActionButtonLayout.xbox_left_stick_click] = true;
                xboxStatus.left_stick_click.Set(true, 1.0f);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.xbox_left_stick_click] == true)
            {
                inputState[ActionButtonLayout.xbox_left_stick_click] = false;
                xboxStatus.left_stick_click.Set();
            }
        }

        [HandleInputAttribute(ActionButtonLayout.xbox_right_trigger)]
        public void inputXBoxRightTrigger(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            xboxStatus.right_trigger.Set();
            if (analog.x > 0)
                xboxStatus.right_trigger.Set(true, analog.x);
        }

        [HandleInputAttribute(ActionButtonLayout.xbox_right_bumper)]
        public void inputXBoxRightBumper(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            xboxStatus.right_bumper.Set();
            if (analog.x > 0)
                xboxStatus.right_bumper.Set(true, analog.x);
        }

        [HandleInputAttribute(ActionButtonLayout.xbox_right_stick_click)]
        public void inputXBoxRightStickClick(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.xbox_right_stick_click] == false)
            {
                inputState[ActionButtonLayout.xbox_right_stick_click] = true;
                xboxStatus.right_stick_click.Set(true, 1.0f);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.xbox_right_stick_click] == true)
            {
                inputState[ActionButtonLayout.xbox_right_stick_click] = false;
                xboxStatus.right_stick_click.Set();
            }
        }

        [HandleInputAttribute(ActionButtonLayout.xbox_pad_up)]
        public void inputXBoxPadUp(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.xbox_pad_up] == false)
            {
                inputState[ActionButtonLayout.xbox_pad_up] = true;
                xboxStatus.dpad_up.Set(true, 1.0f);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.xbox_pad_up] == true)
            {
                inputState[ActionButtonLayout.xbox_pad_up] = false;
                xboxStatus.dpad_up.Set();
            }
        }

        [HandleInputAttribute(ActionButtonLayout.xbox_pad_down)]
        public void inputXBoxPadDown(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.xbox_pad_down] == false)
            {
                inputState[ActionButtonLayout.xbox_pad_down] = true;
                xboxStatus.dpad_down.Set(true, 1.0f);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.xbox_pad_down] == true)
            {
                inputState[ActionButtonLayout.xbox_pad_down] = false;
                xboxStatus.dpad_down.Set();
            }
        }

        [HandleInputAttribute(ActionButtonLayout.xbox_pad_left)]
        public void inputXBoxPadLeft(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.xbox_pad_left] == false)
            {
                inputState[ActionButtonLayout.xbox_pad_left] = true;
                xboxStatus.dpad_left.Set(true, 1.0f);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.xbox_pad_left] == true)
            {
                inputState[ActionButtonLayout.xbox_pad_left] = false;
                xboxStatus.dpad_left.Set();
            }
        }

        [HandleInputAttribute(ActionButtonLayout.xbox_pad_right)]
        public void inputXBoxPadRight(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.xbox_pad_right] == false)
            {
                inputState[ActionButtonLayout.xbox_pad_right] = true;
                xboxStatus.dpad_right.Set(true, 1.0f);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.xbox_pad_right] == true)
            {
                inputState[ActionButtonLayout.xbox_pad_right] = false;
                xboxStatus.dpad_right.Set();
            }
        }

        [HandleInputAttribute(ActionButtonLayout.xbox_start)]
        public void inputXBoxStart(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.xbox_start] == false)
            {
                inputState[ActionButtonLayout.xbox_start] = true;
                xboxStatus.start.Set(true, 1.0f);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.xbox_start] == true)
            {
                inputState[ActionButtonLayout.xbox_start] = false;
                xboxStatus.start.Set();
            }
        }

        [HandleInputAttribute(ActionButtonLayout.xbox_select)]
        public void inputXBoxSelect(InputAnalogActionData analog, InputDigitalActionData digital)
        {
            if (digital.bState == true && inputState[ActionButtonLayout.xbox_select] == false)
            {
                inputState[ActionButtonLayout.xbox_select] = true;
                xboxStatus.select.Set(true, 1.0f);
            }
            else if (digital.bState == false && inputState[ActionButtonLayout.xbox_select] == true)
            {
                inputState[ActionButtonLayout.xbox_select] = false;
                xboxStatus.select.Set();
            }
        }
    }
}
