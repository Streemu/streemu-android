# Componentes de terceiros

O código do Streemu para Android é GPLv3 ([LICENSE](LICENSE)). O app usa os componentes abaixo, cada um com a sua licença.

| Componente | Licença | Versão | Origem |
|---|---|---|---|
| SRT (`libsrt.so`) | MPL-2.0 | v1.5.7 | <https://github.com/Haivision/srt> |
| Mbed TLS (dentro do `libsrt.so`) | Apache-2.0 | v3.6.8 | <https://github.com/Mbed-TLS/mbedtls> |
| .NET para Android | MIT | 10 | <https://github.com/dotnet/android> |

O `libsrt.so` é compilado pelo `nativo/compilar-srt.ps1` a partir do código de origem, nas versões acima.
