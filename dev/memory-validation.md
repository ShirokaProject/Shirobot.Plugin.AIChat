# Memory validation

The console verification suite passes after the streaming changes. Three sends of a cached 4 MiB image allocated 323,001,848 bytes with the former buffered approach versus 28,296 bytes with the streaming content. This measures request construction and serialization, excluding downloads, startup and fixture generation; it is not a prediction of host RSS.

The suite also verifies historical images, tool messages, Base64 padding, cancellation/file closure, and collection of a disposed request's large payload while its HttpContent remains alive. Pending requests cancel and drain during unloading. Debug builds succeed.

An isolated host and the repository DebugAdapter were used with a local mock upstream. The initial chat/unload test retained the assembly. A standalone history serialization reproducer identified a strong reference from System.Text.Json ReflectionEmitCachingMemberAccessor to IEquatable<ChatTurn>. Serializing generated DTOs with their declared type removes that interface-probing path. A regression check round-trips all four polymorphic history part kinds and verifies collectible assembly release across eight loads. Chat/image/tool requests now complete and release the assembly on unloading; repeated host verification is recorded below. The host configuration fingerprint fix removes a verified JSON metadata retention path; targeted configuration reload and collectible configuration checks pass. No SDK/API or persisted configuration format is changed.

The production runtime directory was not modified. No release tag is created by this change.

The fixed plugin passed six DebugAdapter chat/unload cycles (36 messages): five in one Release host process and one in a Debug host. Each cycle alternated cached 4 MiB images and text, exercised tool request/reply rounds, persisted history, and verified the host confirmed assembly release. After the Debug cycle, the isolated host reported 93.4 MiB RSS and 3.8 MiB GC heap. These are local mock-upstream measurements, not a production memory limit.
