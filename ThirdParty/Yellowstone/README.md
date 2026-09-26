# Yellowstone protobuf contract

These minimal protobuf declarations retain only the fields TrenchHQ consumes from
the public Yellowstone gRPC wire contract. They were independently reduced from
`yellowstone-grpc-proto` 12.7.0 at commit
`5e76c224f693ddb71ee442aa55372a9c4a5f9cee`:

https://github.com/rpcpool/yellowstone-grpc/tree/5e76c224f693ddb71ee442aa55372a9c4a5f9cee/yellowstone-grpc-proto

The upstream protobuf component is Apache-2.0. The root Yellowstone server and
plugin workspace is AGPL-3.0; no server/plugin source is included or linked.
Generated C# files are build outputs and are not committed.

