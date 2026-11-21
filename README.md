# Streemu para Android

O app de celular do Streemu: **assista no celular as lives dos amigos** do seu grupo, as mesmas que eles transmitem pelo [Streemu de PC](https://github.com/Streemu/Streemu). O celular só assiste, não transmite.

Este repositório tem o código que roda no seu celular, para quem quer conferir o que ele faz. O servidor do grupo **não** está aqui.

Android 8.0 ou mais novo (64 ou 32 bits).

## O que o app faz

- Entra com a sua conta do Streemu e mostra quem do grupo está ao vivo.
- Conecta direto no PC de quem transmite (P2P, por SRT com criptografia). Quando a rede não deixa, entra um relay de reserva.
- Decodifica o vídeo (H.264) e o som (Opus) com o decodificador do próprio celular (MediaCodec).
- Se a rede do celular mudar (Wi-Fi para 4G, por exemplo), pede a live de novo pela rede nova.

## Permissões

Só duas, e nenhuma pede confirmação:

- `INTERNET`: para receber a live e falar com o servidor do grupo.
- `ACCESS_NETWORK_STATE`: para saber quando a rede muda e reconectar.

O app **não** pede câmera, microfone, localização, contatos, arquivos nem notificações.

## Diagnóstico (o que o app envia)

Na primeira vez, o app mostra um aviso no topo da aba **Ao vivo**. **Nada do diagnóstico sai do celular antes de você responder.** "Entendi" liga, "Desligar" desliga, e dá para mudar quando quiser em **Conta > Diagnóstico**.

Ligado, ele envia para o servidor do Streemu (`app/Telemetria.cs`):

- **O aparelho:** modelo, fabricante, chip, versão do Android, RAM, tela e o que o decodificador de vídeo do celular aguenta.
- **O momento:** tipo de rede (Wi-Fi ou dados móveis, e a operadora), temperatura do aparelho, bateria e RAM livre.
- **As lives que você assistiu:** quando começou e acabou, de quem era, a qualidade e, a cada 2 segundos, números como velocidade, perda, atraso, quadros por segundo e falhas do som.
- **O log do app**, para achar o que deu errado.

Antes de gravar, o app tira dados que identificam a sua rede ou a sua conta: os IPs viram `a.b.x.x`, e tokens e endereços MAC são apagados. O celular é identificado por um código tirado do `ANDROID_ID` com SHA-256, para não misturar com os seus outros aparelhos. Os dados ficam guardados por 120 dias.

O envio foi feito para não pesar em celular fraco: roda em segundo plano com prioridade baixa, em pacotes espaçados, e o que fica guardado no celular tem um limite de 40 MB.

## Compilar

Precisa do .NET 10 com o workload `android` e do Android NDK (para o `libsrt`).

```powershell
dotnet workload install android
.\nativo\compilar-srt.ps1
dotnet build app -c Release
```

O `nativo/compilar-srt.ps1` baixa o [SRT](https://github.com/Haivision/srt) e o [Mbed TLS](https://github.com/Mbed-TLS/mbedtls) nas versões fixadas no script e compila o `libsrt.so` em `app/lib/<arquitetura>/`.

## Pastas

- `app/`: o app (telas, player, decodificador, som, diagnóstico).
- `rede/`: a conexão com o PC e com a sala do grupo, a mesma do app de PC.
- `nativo/`: o script que compila o `libsrt`.

## Segurança e licença

Achou uma falha? Veja o [SECURITY.md](SECURITY.md). O código é GPLv3 ([LICENSE](LICENSE)); os componentes de terceiros estão no [TERCEIROS.md](TERCEIROS.md).
