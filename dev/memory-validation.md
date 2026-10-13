# Memory validation

The console verification suite passes after the streaming changes. Three sends of a cached 4 MiB image allocated 323,001,848 bytes with the former buffered approach versus 28,296 bytes with the streaming content. This measures request construction and serialization, excluding downloads, startup and fixture generation; it is not a prediction of host RSS.

The suite also verifies historical images, tool messages, Base64 padding, cancellation/file closure, and collection of a disposed request's large payload while its HttpContent remains alive. Pending requests cancel and drain during unloading. Debug builds succeed.

An isolated host and the repository DebugAdapter were used with a local mock upstream. Chat/image requests completed and plugin shutdown disposed its resources, but the collectible assembly remained after unloading and additional GC. Full assembly hot unloading is therefore still unresolved. The host configuration fingerprint fix removes a verified JSON metadata retention path; targeted configuration reload and collectible configuration checks pass. No SDK/API or persisted configuration format is changed.

The production runtime directory was not modified. No release tag is created by this change.
