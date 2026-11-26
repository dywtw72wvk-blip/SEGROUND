// Minimal Space Engineers in-game API stubs, used ONLY to syntax/type-check the
// scripts offline (mcs -langversion:6). Signatures mirror the real API for the
// members the Fleet System uses; bodies are dummies. Real builds/tests happen
// in-game (or with MDK2 against the actual game DLLs).
using System;
using System.Collections.Generic;

namespace VRageMath
{
    public struct Vector3
    {
        public float X, Y, Z;
        public Vector3(float x, float y, float z) { X = x; Y = y; Z = z; }
    }

    public struct Vector2
    {
        public float X, Y;
        public Vector2(float x, float y) { X = x; Y = y; }
        public static Vector2 operator +(Vector2 a, Vector2 b) { return new Vector2(a.X + b.X, a.Y + b.Y); }
        public static Vector2 operator -(Vector2 a, Vector2 b) { return new Vector2(a.X - b.X, a.Y - b.Y); }
        public static Vector2 operator *(Vector2 a, float s) { return new Vector2(a.X * s, a.Y * s); }
        public static Vector2 operator /(Vector2 a, float s) { return new Vector2(a.X / s, a.Y / s); }
    }

    public struct Vector3D
    {
        public double X, Y, Z;
        public Vector3D(double x, double y, double z) { X = x; Y = y; Z = z; }
        public Vector3D(double v) { X = Y = Z = v; }
        public Vector3D(Vector3I v) { X = v.X; Y = v.Y; Z = v.Z; }
        public static Vector3D Min(Vector3D a, Vector3D b) { return new Vector3D(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)); }
        public static Vector3D Max(Vector3D a, Vector3D b) { return new Vector3D(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)); }
        public static Vector3D Transform(Vector3D v, MatrixD m) { return v; }
        public static readonly Vector3D Zero = new Vector3D(0, 0, 0);
        public static readonly Vector3D Up = new Vector3D(0, 1, 0);
        public static readonly Vector3D Right = new Vector3D(1, 0, 0);
        public static readonly Vector3D Forward = new Vector3D(0, 0, -1);
        public double Length() { return Math.Sqrt(X * X + Y * Y + Z * Z); }
        public double LengthSquared() { return X * X + Y * Y + Z * Z; }
        public static double Dot(Vector3D a, Vector3D b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
        public static Vector3D Cross(Vector3D a, Vector3D b) { return new Vector3D(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X); }
        public static Vector3D Normalize(Vector3D v) { return v / v.Length(); }
        public static Vector3D TransformNormal(Vector3D v, MatrixD m) { return v; }
        public static bool IsZero(Vector3D v) { return v.LengthSquared() == 0; }
        public static Vector3D operator +(Vector3D a, Vector3D b) { return new Vector3D(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static Vector3D operator -(Vector3D a, Vector3D b) { return new Vector3D(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static Vector3D operator -(Vector3D a) { return new Vector3D(-a.X, -a.Y, -a.Z); }
        public static Vector3D operator *(Vector3D a, double s) { return new Vector3D(a.X * s, a.Y * s, a.Z * s); }
        public static Vector3D operator *(double s, Vector3D a) { return a * s; }
        public static Vector3D operator /(Vector3D a, double s) { return new Vector3D(a.X / s, a.Y / s, a.Z / s); }
        public static implicit operator Vector3D(Vector3 v) { return new Vector3D(v.X, v.Y, v.Z); }
    }

    public struct MatrixD
    {
        public Vector3D Forward, Backward, Up, Down, Left, Right, Translation;
        public static readonly MatrixD Identity = new MatrixD();
        public static MatrixD Transpose(MatrixD m) { return m; }
        public static MatrixD CreateWorld(Vector3D position, Vector3D forward, Vector3D up) { return new MatrixD(); }
    }

    public struct Color
    {
        public byte R, G, B, A;
        public Color(int r, int g, int b) { R = (byte)r; G = (byte)g; B = (byte)b; A = 255; }
        public static bool operator ==(Color a, Color b) { return a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A; }
        public static bool operator !=(Color a, Color b) { return !(a == b); }
        public override bool Equals(object o) { return o is Color && (Color)o == this; }
        public override int GetHashCode() { return R ^ (G << 8) ^ (B << 16); }
    }

    public struct BoundingSphereD { public Vector3D Center; public double Radius; }
    public struct BoundingBoxD { public Vector3D Min, Max; public Vector3D Size { get { return Max - Min; } } }
    public struct Vector3I { public int X, Y, Z; }

    public static class Base6Directions
    {
        public enum Direction : byte { Forward, Backward, Left, Right, Up, Down }
        public static Vector3 GetVector(Direction d) { return new Vector3(); }
    }

    public struct MyBlockOrientation { public Base6Directions.Direction Forward, Up; }
}

namespace VRage
{
    public struct MyFixedPoint { public static explicit operator float(MyFixedPoint v) { return 0; } public static implicit operator MyFixedPoint(int v) { return new MyFixedPoint(); } public static explicit operator MyFixedPoint(float v) { return new MyFixedPoint(); } }

    public struct MyTuple<T1, T2>
    {
        public readonly T1 Item1; public readonly T2 Item2;
        public MyTuple(T1 a, T2 b) { Item1 = a; Item2 = b; }
    }
    public struct MyTuple<T1, T2, T3>
    {
        public readonly T1 Item1; public readonly T2 Item2; public readonly T3 Item3;
        public MyTuple(T1 a, T2 b, T3 c) { Item1 = a; Item2 = b; Item3 = c; }
    }
    public struct MyTuple<T1, T2, T3, T4>
    {
        public readonly T1 Item1; public readonly T2 Item2; public readonly T3 Item3; public readonly T4 Item4;
        public MyTuple(T1 a, T2 b, T3 c, T4 d) { Item1 = a; Item2 = b; Item3 = c; Item4 = d; }
    }
    public struct MyTuple<T1, T2, T3, T4, T5>
    {
        public readonly T1 Item1; public readonly T2 Item2; public readonly T3 Item3; public readonly T4 Item4; public readonly T5 Item5;
        public MyTuple(T1 a, T2 b, T3 c, T4 d, T5 e) { Item1 = a; Item2 = b; Item3 = c; Item4 = d; Item5 = e; }
    }
    public struct MyTuple<T1, T2, T3, T4, T5, T6>
    {
        public readonly T1 Item1; public readonly T2 Item2; public readonly T3 Item3; public readonly T4 Item4; public readonly T5 Item5; public readonly T6 Item6;
        public MyTuple(T1 a, T2 b, T3 c, T4 d, T5 e, T6 f) { Item1 = a; Item2 = b; Item3 = c; Item4 = d; Item5 = e; Item6 = f; }
    }
}

namespace VRage.Game.ModAPI.Ingame
{
    using VRageMath;
    using VRage;

    public interface IMyEntity
    {
        long EntityId { get; }
        MatrixD WorldMatrix { get; }
        bool Closed { get; }
        bool HasInventory { get; }
        int InventoryCount { get; }
        IMyInventory GetInventory(int index);
        Vector3D GetPosition();
    }

    public interface IMyCubeGrid : IMyEntity
    {
        bool IsStatic { get; }
        BoundingSphereD WorldVolume { get; }
        string CustomName { get; set; }
        float GridSize { get; }
        Vector3I Min { get; }
        Vector3I Max { get; }
    }

    public struct MyItemType { public string TypeId { get { return ""; } } public string SubtypeId { get { return ""; } } }
    public struct MyInventoryItem { public MyItemType Type; public MyFixedPoint Amount; public uint ItemId; }
    public interface IMyInventory
    {
        MyFixedPoint CurrentVolume { get; } MyFixedPoint MaxVolume { get; }
        void GetItems(List<MyInventoryItem> items, Func<MyInventoryItem, bool> filter = null);
        bool TransferItemTo(IMyInventory dst, MyInventoryItem item, MyFixedPoint? amount = null);
        bool IsConnectedTo(IMyInventory other);
    }

    public struct SerializableDefinitionId { public string SubtypeName { get { return ""; } } }

    public interface IMyCubeBlock : IMyEntity
    {
        IMyCubeGrid CubeGrid { get; }
        bool IsFunctional { get; }
        bool IsWorking { get; }
        MyBlockOrientation Orientation { get; }
        SerializableDefinitionId BlockDefinition { get; }
        string DefinitionDisplayNameText { get; }
    }
}

namespace VRage.Game.ModAPI.Ingame.Utilities
{
    public struct MyIniParseResult { public bool Success; public string Error; public int LineNo; }

    public struct MyIniValue
    {
        public override string ToString() { return ""; }
        public string ToString(string d) { return d; }
        public bool ToBoolean(bool d = false) { return d; }
        public int ToInt32(int d = 0) { return d; }
        public long ToInt64(long d = 0) { return d; }
        public double ToDouble(double d = 0) { return d; }
    }

    public class MyIni
    {
        public bool TryParse(string content) { return true; }
        public bool TryParse(string content, out MyIniParseResult result) { result = new MyIniParseResult(); return true; }
        public bool ContainsKey(string section, string key) { return false; }
        public MyIniValue Get(string section, string key) { return new MyIniValue(); }
        public void Set(string section, string key, string value) { }
        public void Set(string section, string key, bool value) { }
        public void Set(string section, string key, int value) { }
        public void Set(string section, string key, long value) { }
        public void Set(string section, string key, double value) { }
        public void GetSections(List<string> names) { }
        public void Clear() { }
        public override string ToString() { return ""; }
    }
}

namespace VRage.Game.GUI.TextPanel
{
    using VRageMath;

    public enum SpriteType : byte { TEXTURE, TEXT, CLIP_RECT }
    public enum TextAlignment : byte { LEFT, RIGHT, CENTER }
    public enum ContentType : byte { NONE, TEXT_AND_IMAGE, IMAGE, SCRIPT }

    public struct MySprite
    {
        public SpriteType Type; public string Data; public Vector2? Position; public Vector2? Size; public Color? Color;
        public string FontId; public TextAlignment Alignment; public float RotationOrScale;
        public MySprite(SpriteType type = SpriteType.TEXTURE, string data = null, Vector2? position = null, Vector2? size = null,
                        Color? color = null, string fontId = null, TextAlignment alignment = TextAlignment.CENTER, float rotation = 0)
        { Type = type; Data = data; Position = position; Size = size; Color = color; FontId = fontId; Alignment = alignment; RotationOrScale = rotation; }
        public static MySprite CreateText(string text, string fontId, Color color, float scale = 1, TextAlignment alignment = TextAlignment.CENTER)
        { return new MySprite(SpriteType.TEXT, text, null, null, color, fontId, alignment, scale); }
    }

    public struct MySpriteDrawFrame : IDisposable
    {
        public void Add(MySprite sprite) { }
        public void Dispose() { }
    }
}

namespace Sandbox.ModAPI.Ingame
{
    using VRageMath;
    using VRage.Game.ModAPI.Ingame;
    using VRage.Game.GUI.TextPanel;

    [Flags] public enum UpdateType { None = 0, Terminal = 1, Trigger = 2, Mod = 8, Script = 16, Update1 = 32, Update10 = 64, Update100 = 128, Once = 256, IGC = 512 }
    [Flags] public enum UpdateFrequency : byte { None = 0, Update1 = 1, Update10 = 2, Update100 = 4, Once = 8 }
    public enum TransmissionDistance { CurrentConstruct, ConnectedConstructs, AntennaRelay }
    public enum MyShipConnectorStatus { Unconnected, Connectable, Connected }
    public enum ChargeMode { Auto, Recharge, Discharge }

    public interface IMyGridProgramRuntimeInfo
    {
        TimeSpan TimeSinceLastRun { get; }
        double LastRunTimeMs { get; }
        UpdateFrequency UpdateFrequency { get; set; }
    }

    public struct MyIGCMessage { public object Data; public string Tag; public long Source; }
    public interface IMyMessageProvider { bool HasPendingMessage { get; } MyIGCMessage AcceptMessage(); }
    public interface IMyBroadcastListener : IMyMessageProvider { string Tag { get; } }
    public interface IMyUnicastListener : IMyMessageProvider { }

    public interface IMyIntergridCommunicationSystem
    {
        long Me { get; }
        IMyUnicastListener UnicastListener { get; }
        IMyBroadcastListener RegisterBroadcastListener(string tag);
        void DisableBroadcastListener(IMyBroadcastListener listener);
        void SendBroadcastMessage<TData>(string tag, TData data, TransmissionDistance range = TransmissionDistance.AntennaRelay);
        bool SendUnicastMessage<TData>(long addressee, string tag, TData data);
    }

    public interface IMyGridTerminalSystem
    {
        void GetBlocksOfType<T>(List<T> blocks, Func<T, bool> collect = null) where T : class;
    }

    public interface IMyTerminalBlock : IMyCubeBlock
    {
        string CustomName { get; set; }
        string CustomData { get; set; }
        bool IsSameConstructAs(IMyTerminalBlock other);
        Sandbox.ModAPI.Interfaces.ITerminalProperty GetProperty(string id);
    }

    public interface IMyFunctionalBlock : IMyTerminalBlock { bool Enabled { get; set; } }

    public interface IMyTextSurface
    {
        ContentType ContentType { get; set; }
        string Script { get; set; }
        Color ScriptBackgroundColor { get; set; }
        Vector2 SurfaceSize { get; }
        Vector2 TextureSize { get; }
        MySpriteDrawFrame DrawFrame();
        float FontSize { get; set; }
        string Font { get; set; }
        bool WriteText(string value, bool append = false);
    }

    public interface IMyTextSurfaceProvider { int SurfaceCount { get; } IMyTextSurface GetSurface(int index); }

    public interface IMyProgrammableBlock : IMyFunctionalBlock, IMyTextSurfaceProvider { bool TryRun(string argument); }

    public struct MyShipVelocities { public readonly Vector3D LinearVelocity, AngularVelocity; }
    public struct MyShipMass { public readonly float BaseMass, TotalMass, PhysicalMass; }

    public interface IMyShipController : IMyTerminalBlock
    {
        Vector3D CenterOfMass { get; }
        bool DampenersOverride { get; set; }
        bool IsMainCockpit { get; set; }
        MyShipVelocities GetShipVelocities();
        Vector3D GetNaturalGravity();
        MyShipMass CalculateShipMass();
        bool TryGetPlanetElevation(MyPlanetElevation detail, out double elevation);
    }
    public enum MyPlanetElevation { Sealevel, Surface }
    public interface IMyRemoteControl : IMyShipController { }

    public interface IMyThrust : IMyFunctionalBlock { float ThrustOverridePercentage { get; set; } float MaxEffectiveThrust { get; } }
    public interface IMyGyro : IMyFunctionalBlock { float Pitch { get; set; } float Yaw { get; set; } float Roll { get; set; } bool GyroOverride { get; set; } }
    public interface IMyShipConnector : IMyFunctionalBlock
    {
        MyShipConnectorStatus Status { get; }
        IMyShipConnector OtherConnector { get; }
        bool ThrowOut { get; set; }
        void Connect();
        void Disconnect();
    }
    public interface IMyBatteryBlock : IMyFunctionalBlock { float CurrentStoredPower { get; } float MaxStoredPower { get; } ChargeMode ChargeMode { get; set; } }
    public interface IMyGasTank : IMyFunctionalBlock { double FilledRatio { get; } float Capacity { get; } bool Stockpile { get; set; } }
    public interface IMyCargoContainer : IMyTerminalBlock { }
    public interface IMyShipDrill : IMyFunctionalBlock { }
    public interface IMyOreDetector : IMyFunctionalBlock { float Range { get; } }
    public interface IMyRadioAntenna : IMyFunctionalBlock { bool EnableBroadcasting { get; set; } float Radius { get; set; } }
    public interface IMyUserControllableGun : IMyFunctionalBlock { }
    public interface IMyLargeTurretBase : IMyUserControllableGun { bool HasTarget { get; } MyDetectedEntityInfo GetTargetedEntity(); }
    public interface IMyCameraBlock : IMyFunctionalBlock
    {
        bool EnableRaycast { get; set; }
        double AvailableScanRange { get; }
        bool CanScan(double distance);
        MyDetectedEntityInfo Raycast(double distance, float pitch = 0, float yaw = 0);
        MyDetectedEntityInfo Raycast(Vector3D targetPos);
    }
    public interface IMyTurretControlBlock : IMyFunctionalBlock { bool HasTarget { get; } MyDetectedEntityInfo GetTargetedEntity(); }
    public interface IMySearchEnemyComponent { long FoundEnemyId { get; } }
    public interface IMyOffensiveCombatBlock : IMyFunctionalBlock { IMySearchEnemyComponent SearchEnemyComponent { get; } }
    public interface IMyFlightMovementBlock : IMyFunctionalBlock { }
    public interface IMyWarhead : IMyTerminalBlock { bool IsArmed { get; set; } void Detonate(); }
    public interface IMyProjector : IMyFunctionalBlock { bool IsProjecting { get; } int RemainingBlocks { get; } int TotalBlocks { get; } }
    public interface IMyShipWelder : IMyFunctionalBlock { }
    public interface IMyJumpDrive : IMyFunctionalBlock { }
    public enum MyDetectedEntityType { None, Unknown, SmallGrid, LargeGrid, CharacterHuman, CharacterOther, FloatingObject, Asteroid, Planet, Meteor, Missile }
    public struct MyDetectedEntityInfo
    {
        public long EntityId; public VRage.Game.MyRelationsBetweenPlayerAndBlock Relationship;
        public MyDetectedEntityType Type; public Vector3D Position; public Vector3 Velocity; public BoundingBoxD BoundingBox; public Vector3D? HitPosition;
        public bool IsEmpty() { return EntityId == 0; }
    }
    public interface IMySensorBlock : IMyFunctionalBlock { void DetectedEntities(List<MyDetectedEntityInfo> entities); }

    public abstract class MyGridProgram
    {
        protected IMyGridTerminalSystem GridTerminalSystem { get; set; }
        protected IMyProgrammableBlock Me { get; set; }
        protected IMyGridProgramRuntimeInfo Runtime { get; set; }
        protected string Storage { get; set; }
        protected IMyIntergridCommunicationSystem IGC { get; set; }
        protected Action<string> Echo { get; set; }
    }
}

namespace SpaceEngineers.Game.ModAPI.Ingame
{
    using Sandbox.ModAPI.Ingame;
    public interface IMyLandingGear : IMyFunctionalBlock { bool IsLocked { get; } }
    public interface IMyTimerBlock : IMyFunctionalBlock { void Trigger(); void StartCountdown(); }
    public interface IMyShipMergeBlock : IMyFunctionalBlock { bool IsConnected { get; } }
}

namespace VRage.Game
{
    public enum MyRelationsBetweenPlayerAndBlock { NoOwnership, Owner, FactionShare, Neutral, Enemies, Friends }
}

namespace Sandbox.ModAPI.Interfaces
{
    public interface ITerminalProperty { string Id { get; } }
    public interface ITerminalProperty<TValue> : ITerminalProperty { TValue GetValue(VRage.Game.ModAPI.Ingame.IMyCubeBlock block); }
    public static class TerminalPropertyExtensions { public static ITerminalProperty<T> As<T>(this ITerminalProperty p) { return null; } }
}
