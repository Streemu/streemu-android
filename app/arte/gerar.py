"""Gera as imagens do app Android a partir da arte do Streemu (app/Streemu/Arte do repositório de trabalho).

Uso: python app/arte/gerar.py <pasta Arte do Streemu>
Reduz cada imagem para o maior tamanho que a tela usa (a original tem até 1024 px e 800 KB) e monta o ícone adaptativo:
o gato no meio da área segura (66 de 108 dp), sobre o roxo do fundo da janela.
"""
import os
import sys
from PIL import Image

arte = sys.argv[1]
raiz = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Resources")

def salvar(img, pasta, nome):
    d = os.path.join(raiz, pasta)
    os.makedirs(d, exist_ok=True)
    if nome.endswith(".webp"): img.save(os.path.join(d, nome), quality=88, method=6)   # ~1/6 do PNG, sem diferença visível
    else: img.save(os.path.join(d, nome), optimize=True)

def reduzir(nome, largura):
    img = Image.open(os.path.join(arte, nome)).convert("RGBA")
    h = round(img.height * largura / img.width)
    return img.resize((largura, h), Image.LANCZOS)

# drawable-nodpi: a tela decide o tamanho em dp; estes são os tamanhos máximos em pixels num celular xxxhdpi
salvar(reduzir("mumu-controle.png", 520), "drawable-nodpi", "mumu_controle.webp")
salvar(reduzir("mumu-dormindo.png", 400), "drawable-nodpi", "mumu_dormindo.webp")
salvar(reduzir("mumu-pipoca.png", 400), "drawable-nodpi", "mumu_pipoca.webp")
salvar(reduzir("gato.png", 192), "drawable-nodpi", "gato.webp")

# ícone adaptativo: 108 dp de lado, área segura de 66 dp no centro
gato = Image.open(os.path.join(arte, "gato.png")).convert("RGBA")
for pasta, lado in [("mipmap-mdpi", 108), ("mipmap-hdpi", 162), ("mipmap-xhdpi", 216), ("mipmap-xxhdpi", 324), ("mipmap-xxxhdpi", 432)]:
    frente = Image.new("RGBA", (lado, lado), (0, 0, 0, 0))
    g = round(lado * 64 / 108)
    frente.alpha_composite(gato.resize((g, g), Image.LANCZOS), ((lado - g) // 2, (lado - g) // 2))
    salvar(frente, pasta, "appicon_foreground.png")
    salvar(Image.new("RGBA", (lado, lado), (0x2A, 0x26, 0x40, 255)), pasta, "appicon_background.png")
    # ícone antigo (antes do Android 8): o mesmo, já composto
    velho = Image.new("RGBA", (lado, lado), (0x2A, 0x26, 0x40, 255))
    velho.alpha_composite(frente)
    salvar(velho.resize((lado * 48 // 108, lado * 48 // 108), Image.LANCZOS), pasta, "appicon.png")
print("ok")
