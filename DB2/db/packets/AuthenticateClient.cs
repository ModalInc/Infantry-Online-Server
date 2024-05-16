// <auto-generated>
//  automatically generated by the FlatBuffers compiler, do not modify
// </auto-generated>

namespace db.packets
{

using global::System;
using global::System.Collections.Generic;
using global::Google.FlatBuffers;

public struct AuthenticateClient : IFlatbufferObject
{
  private Table __p;
  public ByteBuffer ByteBuffer { get { return __p.bb; } }
  public static void ValidateVersion() { FlatBufferConstants.FLATBUFFERS_23_5_26(); }
  public static AuthenticateClient GetRootAsAuthenticateClient(ByteBuffer _bb) { return GetRootAsAuthenticateClient(_bb, new AuthenticateClient()); }
  public static AuthenticateClient GetRootAsAuthenticateClient(ByteBuffer _bb, AuthenticateClient obj) { return (obj.__assign(_bb.GetInt(_bb.Position) + _bb.Position, _bb)); }
  public static bool VerifyAuthenticateClient(ByteBuffer _bb) {Google.FlatBuffers.Verifier verifier = new Google.FlatBuffers.Verifier(_bb); return verifier.VerifyBuffer("", false, AuthenticateClientVerify.Verify); }
  public void __init(int _i, ByteBuffer _bb) { __p = new Table(_i, _bb); }
  public AuthenticateClient __assign(int _i, ByteBuffer _bb) { __init(_i, _bb); return this; }

  public int MsgId { get { int o = __p.__offset(4); return o != 0 ? __p.bb.GetInt(o + __p.bb_pos) : (int)0; } }
  public string Token { get { int o = __p.__offset(6); return o != 0 ? __p.__string(o + __p.bb_pos) : null; } }
#if ENABLE_SPAN_T
  public Span<byte> GetTokenBytes() { return __p.__vector_as_span<byte>(6, 1); }
#else
  public ArraySegment<byte>? GetTokenBytes() { return __p.__vector_as_arraysegment(6); }
#endif
  public byte[] GetTokenArray() { return __p.__vector_as_array<byte>(6); }

  public static Offset<db.packets.AuthenticateClient> CreateAuthenticateClient(FlatBufferBuilder builder,
      int msg_id = 0,
      StringOffset tokenOffset = default(StringOffset)) {
    builder.StartTable(2);
    AuthenticateClient.AddToken(builder, tokenOffset);
    AuthenticateClient.AddMsgId(builder, msg_id);
    return AuthenticateClient.EndAuthenticateClient(builder);
  }

  public static void StartAuthenticateClient(FlatBufferBuilder builder) { builder.StartTable(2); }
  public static void AddMsgId(FlatBufferBuilder builder, int msgId) { builder.AddInt(0, msgId, 0); }
  public static void AddToken(FlatBufferBuilder builder, StringOffset tokenOffset) { builder.AddOffset(1, tokenOffset.Value, 0); }
  public static Offset<db.packets.AuthenticateClient> EndAuthenticateClient(FlatBufferBuilder builder) {
    int o = builder.EndTable();
    return new Offset<db.packets.AuthenticateClient>(o);
  }
  public static void FinishAuthenticateClientBuffer(FlatBufferBuilder builder, Offset<db.packets.AuthenticateClient> offset) { builder.Finish(offset.Value); }
  public static void FinishSizePrefixedAuthenticateClientBuffer(FlatBufferBuilder builder, Offset<db.packets.AuthenticateClient> offset) { builder.FinishSizePrefixed(offset.Value); }
}


static public class AuthenticateClientVerify
{
  static public bool Verify(Google.FlatBuffers.Verifier verifier, uint tablePos)
  {
    return verifier.VerifyTableStart(tablePos)
      && verifier.VerifyField(tablePos, 4 /*MsgId*/, 4 /*int*/, 4, false)
      && verifier.VerifyString(tablePos, 6 /*Token*/, false)
      && verifier.VerifyTableEnd(tablePos);
  }
}

}