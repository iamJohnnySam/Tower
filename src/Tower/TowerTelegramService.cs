using Grpc.Core;
using Tower.Telegram.Grpc;

namespace Tower;

/// <summary>
/// Dormant stub gRPC service. All methods are unimplemented until Task 5.
/// Placed in the web project (src/Tower/) because it requires the server-stub
/// types generated from the proto (GrpcServices="Server"), which are only
/// available to the project that holds the Protobuf item + Grpc.AspNetCore ref.
/// </summary>
public sealed class TowerTelegramService : TowerTelegram.TowerTelegramBase
{
    public override Task StreamUpdates(
        StreamRequest request,
        IServerStreamWriter<Update> responseStream,
        ServerCallContext context) =>
        throw new RpcException(new Status(StatusCode.Unimplemented, "not yet"));

    public override Task<Ack> SyncSubscribers(
        SubscriberList request,
        ServerCallContext context) =>
        throw new RpcException(new Status(StatusCode.Unimplemented, "not yet"));

    public override Task<SendResult> SendMessage(
        SendMessageRequest request,
        ServerCallContext context) =>
        throw new RpcException(new Status(StatusCode.Unimplemented, "not yet"));

    public override Task<SendResult> SendPhoto(
        SendPhotoRequest request,
        ServerCallContext context) =>
        throw new RpcException(new Status(StatusCode.Unimplemented, "not yet"));

    public override Task<SendResult> SendInlineKeyboard(
        InlineKeyboardRequest request,
        ServerCallContext context) =>
        throw new RpcException(new Status(StatusCode.Unimplemented, "not yet"));

    public override Task<SendResult> EditMessage(
        EditMessageRequest request,
        ServerCallContext context) =>
        throw new RpcException(new Status(StatusCode.Unimplemented, "not yet"));

    public override Task<Ack> AnswerCallback(
        AnswerCallbackRequest request,
        ServerCallContext context) =>
        throw new RpcException(new Status(StatusCode.Unimplemented, "not yet"));

    public override Task<SubscriberList> ListSubscribers(
        Empty request,
        ServerCallContext context) =>
        throw new RpcException(new Status(StatusCode.Unimplemented, "not yet"));
}
