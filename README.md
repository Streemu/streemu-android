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

- `INTERNET`: para receber a live, falar com o servidor do grupo e baixar atualizações do GitHub.
- `ACCESS_NETWORK_STATE`: para saber quando a rede muda e reconectar.
- `REQUEST_INSTALL_PACKAGES`: para pedir ao Android que instale uma atualização do Streemu. O Android pode pedir que você permita instalações pelo Streemu nas configurações.

O app **não** pede câmera, microfone, localização, contatos, arquivos nem notificações.

## Atualizações

O Streemu consulta o [manifesto da release mais nova](https://github.com/Streemu/streemu-android/releases/latest/download/atualizacao.json) diretamente no GitHub. Ele espera a live acabar para procurar ou baixar uma atualização. No Wi-Fi, baixa em segundo plano. Se estiver em dados móveis, mostra o tamanho e pede sua autorização antes de baixar. Você também pode procurar em **Conta > Sobre**.

O app confere o APK antes de pedir a instalação. O Android sempre pede confirmação. Se você ainda usa o protótipo assinado com a chave de desenvolvimento, desinstale-o uma vez, instale o primeiro APK oficial e entre na sua conta de novo.

Para conferir um APK baixado manualmente, baixe também o `SHA256SUMS.txt` da mesma release. Compare o hash do arquivo e verifique o atestado de compilação do GitHub.

```powershell
Get-FileHash -Algorithm SHA256 Streemu-Android.apk
gh attestation verify Streemu-Android.apk -R Streemu/streemu-android
```

## Versões disponíveis

- `Streemu-Android.apk` é o único APK. Ele se atualiza sozinho pelo [manifesto da última release](https://github.com/Streemu/streemu-android/releases/latest/download/atualizacao.json) e confere o SHA-256 e a assinatura antes de instalar.

## O que o diagnóstico envia

O diagnóstico vem **ligado**. Na primeira vez, o app mostra um aviso no topo da aba **Ao vivo**: "Entendi" mantém ligado, "Desligar" desliga, e dá para mudar quando quiser em **Conta > Diagnóstico**. Desligado, nada é gravado nem enviado, e o que ainda não tinha subido é apagado do celular.

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
dotnet build app -c Debug
```

O `nativo/compilar-srt.ps1` baixa o [SRT](https://github.com/Haivision/srt) e o [Mbed TLS](https://github.com/Mbed-TLS/mbedtls) nas versões fixadas no script e compila o `libsrt.so` em `app/lib/<arquitetura>/`.

Para compilar em Release, configure `STREEMU_ANDROID_KEYSTORE`, `STREEMU_ANDROID_KEY_ALIAS`, `STREEMU_ANDROID_STORE_PASSWORD` e `STREEMU_ANDROID_KEY_PASSWORD` no ambiente e execute `dotnet publish app -c Release`. Para atualizar um APK existente, os dois precisam ter a mesma assinatura.

## Pastas

- `app/`: o app (telas, player, decodificador, som, diagnóstico).
- `rede/`: a conexão com o PC e com a sala do grupo, a mesma do app de PC.
- `nativo/`: o script que compila o `libsrt`.

## Segurança e licença

Achou uma falha? Veja o [SECURITY.md](SECURITY.md). O código é GPLv3 ([LICENSE](LICENSE)); os componentes de terceiros estão no [TERCEIROS.md](TERCEIROS.md).
