from PIL import Image, ImageDraw
import os

def create_icons():
    base_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    logo_path = os.path.join(base_dir, 'assets', 'logo.png')
    icons_dir = os.path.join(base_dir, 'assets', 'icons')
    os.makedirs(icons_dir, exist_ok=True)

    img = Image.open(logo_path).convert("RGBA")

    # 1. Main app icon (multi-size)
    sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    img.save(os.path.join(icons_dir, 'app.ico'), format='ICO', sizes=sizes)

    # 2. Tray icons (16x16, 32x32, 48x48) with status dots
    for status, color in [
        ('active', (16, 185, 129, 255)),  # emerald/green
        ('idle', (148, 163, 184, 255)),   # slate gray
        ('error', (239, 68, 68, 255))     # red
    ]:
        status_sizes = [(16, 16), (24, 24), (32, 32), (48, 48)]
        images = []
        for s in status_sizes:
            resized = img.resize(s, Image.Resampling.LANCZOS)
            draw = ImageDraw.Draw(resized)
            # Dot radius & pos in bottom right
            r = max(2, s[0] // 4)
            cx, cy = s[0] - r - 1, s[1] - r - 1
            # Outline
            draw.ellipse([cx - r - 1, cy - r - 1, cx + r + 1, cy + r + 1], fill=(15, 23, 42, 255))
            # Status dot
            draw.ellipse([cx - r, cy - r, cx + r, cy + r], fill=color)
            images.append(resized)
        
        images[0].save(os.path.join(icons_dir, f'tray_{status}.ico'), format='ICO', sizes=status_sizes)

    print("Icons successfully generated in assets/icons/")

if __name__ == '__main__':
    create_icons()
