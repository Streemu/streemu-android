# Compila o libsrt (com criptografia pelo mbedtls) para o Android, com o NDK. Sai em app/lib/<abi>/libsrt.so, que o app empacota.
# As fontes são baixadas do GitHub nas versões fixadas abaixo, para nativo/fontes (fora do git), e compiladas em nativo/obj.
# Uso: powershell -File nativo/compilar-srt.ps1   (precisa do NDK, do CMake e do Ninja)
param(
    [string]$Ndk = "",
    [string[]]$Abis = @("arm64-v8a", "armeabi-v7a"),
    [int]$Api = 26
)
$ErrorActionPreference = "Stop"
$SrtTag = "v1.5.7"
$MbedTag = "v3.6.8"   # LTS

$raiz = Split-Path -Parent $PSScriptRoot
$fontes = Join-Path $PSScriptRoot "fontes"
$obj = Join-Path $PSScriptRoot "obj"
if (-not $Ndk) {
    $pasta = Join-Path $env:LOCALAPPDATA "Android\Sdk\ndk"
    $Ndk = (Get-ChildItem $pasta -Directory | Sort-Object { [version]$_.Name } | Select-Object -Last 1).FullName
}
$toolchain = Join-Path $Ndk "build\cmake\android.toolchain.cmake"
if (-not (Test-Path $toolchain)) { throw "NDK não encontrado ($Ndk)" }
Write-Host "NDK: $Ndk"

function Clonar($url, $tag, $destino) {
    if (Test-Path $destino) {
        $atual = git -C $destino describe --tags 2>$null
        if ($atual -eq $tag) { return }
        Remove-Item -Recurse -Force $destino
    }
    # o mbedtls 3.6 precisa do submódulo "framework" até para configurar
    git -c advice.detachedHead=false clone --quiet --depth 1 --recurse-submodules --shallow-submodules --branch $tag $url $destino
    if ($LASTEXITCODE -ne 0) { throw "git clone $url falhou" }
}
New-Item -ItemType Directory -Force $fontes | Out-Null
Clonar "https://github.com/Mbed-TLS/mbedtls.git" $MbedTag (Join-Path $fontes "mbedtls")
Clonar "https://github.com/Haivision/srt.git" $SrtTag (Join-Path $fontes "srt")

function Invoke-CMakeStep([string[]]$a) { & cmake @a; if ($LASTEXITCODE -ne 0) { throw "cmake falhou: $a" } }

foreach ($abi in $Abis) {
    $comum = @("-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", "-DCMAKE_TOOLCHAIN_FILE=$toolchain",
               "-DANDROID_ABI=$abi", "-DANDROID_PLATFORM=android-$Api", "-DANDROID_STL=c++_static",
               "-DCMAKE_POSITION_INDEPENDENT_CODE=ON")
    $prefixo = Join-Path $obj "$abi\prefixo"

    # mbedtls estático, só a biblioteca (sem testes nem programas)
    $bm = Join-Path $obj "$abi\mbedtls"
    Invoke-CMakeStep ($comum + @("-S", (Join-Path $fontes "mbedtls"), "-B", $bm, "-DCMAKE_INSTALL_PREFIX=$prefixo",
                      "-DENABLE_TESTING=OFF", "-DENABLE_PROGRAMS=OFF", "-DUSE_SHARED_MBEDTLS_LIBRARY=OFF", "-DUSE_STATIC_MBEDTLS_LIBRARY=ON"))
    Invoke-CMakeStep @("--build", $bm, "--target", "install")

    # srt compartilhado (libsrt.so), com o mbedtls e o libc++ dentro: o app só carrega este arquivo
    $bs = Join-Path $obj "$abi\srt"
    Invoke-CMakeStep ($comum + @("-S", (Join-Path $fontes "srt"), "-B", $bs, "-DCMAKE_INSTALL_PREFIX=$prefixo", "-DCMAKE_PREFIX_PATH=$prefixo",
                      "-DENABLE_ENCRYPTION=ON", "-DUSE_ENCLIB=mbedtls",
                      # caminhos diretos: o toolchain do Android restringe o find_package ao sysroot e o FindMbedTLS do SRT não acharia o prefixo.
                      # Com SSL_LIBRARY_DIRS preenchido, o CMakeLists do SRT pula a busca e usa estes.
                      "-DSSL_INCLUDE_DIRS=$prefixo/include", "-DSSL_LIBRARY_DIRS=$prefixo/lib",
                      "-DSSL_LIBRARIES=$prefixo/lib/libmbedtls.a;$prefixo/lib/libmbedx509.a;$prefixo/lib/libmbedcrypto.a;$prefixo/lib/libeverest.a;$prefixo/lib/libp256m.a",
                      "-DENABLE_SHARED=ON", "-DENABLE_STATIC=OFF", "-DENABLE_APPS=OFF", "-DENABLE_LOGGING=ON", "-DENABLE_BONDING=OFF"))
    Invoke-CMakeStep @("--build", $bs)

    $saida = Join-Path $raiz "app\lib\$abi"
    New-Item -ItemType Directory -Force $saida | Out-Null
    $so = Get-ChildItem $bs -Filter "libsrt.so*" | Where-Object { -not $_.PSIsContainer -and $_.Name -match '^libsrt\.so(\.\d+)*$' } | Sort-Object Length -Descending | Select-Object -First 1
    $strip = Join-Path $Ndk "toolchains\llvm\prebuilt\windows-x86_64\bin\llvm-strip.exe"
    & $strip --strip-unneeded -o (Join-Path $saida "libsrt.so") $so.FullName
    Write-Host "OK: $saida\libsrt.so ($([math]::Round((Get-Item (Join-Path $saida 'libsrt.so')).Length / 1KB)) KB, SRT $SrtTag + mbedtls $MbedTag)"
}
