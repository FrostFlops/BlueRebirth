using System.Runtime.CompilerServices;

// 内嵌服务端需要直接使用服务端的内部组合根（ServerOptions / ServerHostBuilder），
// 避免把这些类型提升为公共 API。
[assembly: InternalsVisibleTo("BlueOath.Android")]
