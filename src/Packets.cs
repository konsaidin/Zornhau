using ProtoBuf;

namespace WarbandCombat;

[ProtoContract]
public sealed class InputPacket
{
    [ProtoMember(1)] public int Command { get; set; }
    [ProtoMember(2)] public int Direction { get; set; }
    [ProtoMember(3)] public int Sequence { get; set; }
    [ProtoMember(5)] public long SentAt { get; set; }
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
}
