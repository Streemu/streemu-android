// O build de teste tem ícone próprio (fundo laranja e selo "DEV", res/mipmap-anydpi-v26/appicon_dev.xml) para não ser confundido com o app do público ao lado dele na tela.
#if STREEMU_DEV
[assembly: Android.App.Application(Label = Streemu.Proto.NomeApp.Nome, Icon = "@mipmap/appicon_dev", RoundIcon = "@mipmap/appicon_dev")]
#else
[assembly: Android.App.Application(Label = Streemu.Proto.NomeApp.Nome)]
#endif

namespace Streemu.Proto;

/// <summary>O nome na tela inicial. O build de dev (ApplicationId br.com.streemu.proto.spike) define STREEMU_DEV.</summary>
static class NomeApp
{
#if STREEMU_DEV
    public const string Nome = "Streemu DEV";
#else
    public const string Nome = "Streemu";
#endif
}
