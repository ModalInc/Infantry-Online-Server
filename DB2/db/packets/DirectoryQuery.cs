// <auto-generated>
//  automatically generated by the FlatBuffers compiler, do not modify
// </auto-generated>

namespace db.packets
{

using global::System;
using global::System.Collections.Generic;
using global::Google.FlatBuffers;

public struct DirectoryQuery : IFlatbufferObject
{
  private Table __p;
  public ByteBuffer ByteBuffer { get { return __p.bb; } }
  public static void ValidateVersion() { FlatBufferConstants.FLATBUFFERS_23_5_26(); }
  public static DirectoryQuery GetRootAsDirectoryQuery(ByteBuffer _bb) { return GetRootAsDirectoryQuery(_bb, new DirectoryQuery()); }
  public static DirectoryQuery GetRootAsDirectoryQuery(ByteBuffer _bb, DirectoryQuery obj) { return (obj.__assign(_bb.GetInt(_bb.Position) + _bb.Position, _bb)); }
  public static bool VerifyDirectoryQuery(ByteBuffer _bb) {Google.FlatBuffers.Verifier verifier = new Google.FlatBuffers.Verifier(_bb); return verifier.VerifyBuffer("", false, DirectoryQueryVerify.Verify); }
  public void __init(int _i, ByteBuffer _bb) { __p = new Table(_i, _bb); }
  public DirectoryQuery __assign(int _i, ByteBuffer _bb) { __init(_i, _bb); return this; }

  public int MsgId { get { int o = __p.__offset(4); return o != 0 ? __p.bb.GetInt(o + __p.bb_pos) : (int)0; } }

  public static Offset<db.packets.DirectoryQuery> CreateDirectoryQuery(FlatBufferBuilder builder,
      int msg_id = 0) {
    builder.StartTable(1);
    DirectoryQuery.AddMsgId(builder, msg_id);
    return DirectoryQuery.EndDirectoryQuery(builder);
  }

  public static void StartDirectoryQuery(FlatBufferBuilder builder) { builder.StartTable(1); }
  public static void AddMsgId(FlatBufferBuilder builder, int msgId) { builder.AddInt(0, msgId, 0); }
  public static Offset<db.packets.DirectoryQuery> EndDirectoryQuery(FlatBufferBuilder builder) {
    int o = builder.EndTable();
    return new Offset<db.packets.DirectoryQuery>(o);
  }
  public static void FinishDirectoryQueryBuffer(FlatBufferBuilder builder, Offset<db.packets.DirectoryQuery> offset) { builder.Finish(offset.Value); }
  public static void FinishSizePrefixedDirectoryQueryBuffer(FlatBufferBuilder builder, Offset<db.packets.DirectoryQuery> offset) { builder.FinishSizePrefixed(offset.Value); }
}


static public class DirectoryQueryVerify
{
  static public bool Verify(Google.FlatBuffers.Verifier verifier, uint tablePos)
  {
    return verifier.VerifyTableStart(tablePos)
      && verifier.VerifyField(tablePos, 4 /*MsgId*/, 4 /*int*/, 4, false)
      && verifier.VerifyTableEnd(tablePos);
  }
}

}
