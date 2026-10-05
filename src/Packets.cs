using ProtoBuf;

namespace WarbandCombat;

[ProtoContract]
public sealed class InputPacket
{
    [ProtoMember(1)] public int Command { get; set; }
    [ProtoMember(2)] public int Direction { get; set; }
    [ProtoMember(3)] public int Sequence { get; set; }
    [ProtoMember(4)] public string WeaponMode { get; set; } = "";
    [ProtoMember(5)] public long SentAt { get; set; }
    [ProtoMember(6)] public bool HitDiagnostics { get; set; }
}

[ProtoContract]
public sealed class StatePacket
{
    [ProtoMember(1)] public int Phase { get; set; }
    [ProtoMember(2)] public int Direction { get; set; }
    [ProtoMember(3)] public int Elapsed { get; set; }
    [ProtoMember(4)] public int HitIn { get; set; }
    [ProtoMember(5)] public int EndsIn { get; set; }
    [ProtoMember(6)] public float Charge { get; set; }
    [ProtoMember(7)] public int Sequence { get; set; }
    [ProtoMember(8)] public string Feedback { get; set; } = "";
    [ProtoMember(9)] public bool ForcePvpCombat { get; set; }
    [ProtoMember(10)] public bool RecoveryAttackPose { get; set; }
    [ProtoMember(11)] public int ContactEndsIn { get; set; }
    [ProtoMember(12)] public long EchoSentAt { get; set; }
    [ProtoMember(13)] public long InputReceivedAt { get; set; }
    [ProtoMember(14)] public long ServerSentAt { get; set; }
    [ProtoMember(15)] public int SprintIn { get; set; }
}

public enum ContactKind { Hit, Parry, Shield, ShieldBreak }

[ProtoContract]
public sealed class ContactPacket
{
    [ProtoMember(1)] public long Id { get; set; }
    [ProtoMember(2)] public long Attacker { get; set; }
    [ProtoMember(3)] public long Defender { get; set; }
    [ProtoMember(4)] public ContactKind Kind { get; set; }
}

[ProtoContract]
public sealed class DiagnosticsSettingsPacket
{
    [ProtoMember(1)] public bool Enabled { get; set; }
}

[ProtoContract]
public sealed class HitDiagnosticPacket
{
    [ProtoMember(1)] public int AttackId { get; set; }
    [ProtoMember(2)] public string Result { get; set; } = "";
    [ProtoMember(3)] public string Detail { get; set; } = "";
    [ProtoMember(4)] public long Target { get; set; }
}

// No damage, radius or timing claims: those come from the held server item and Fighter.
[ProtoContract]
public sealed class BladeContactPacket
{
    [ProtoMember(1)] public int AttackId { get; set; }
    [ProtoMember(2)] public long Target { get; set; }
    [ProtoMember(3)] public int Blade { get; set; }
    [ProtoMember(4)] public double[] Previous { get; set; } = Array.Empty<double>();
    [ProtoMember(5)] public double[] Current { get; set; } = Array.Empty<double>();
    [ProtoMember(6)] public double Fraction { get; set; }
    [ProtoMember(7)] public double[] Point { get; set; } = Array.Empty<double>();
}
