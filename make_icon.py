"""앱 아이콘(icon.ico) 생성: python make_icon.py — 파란 둥근 사각형 + 흰 코일(인덕터 기호) + 노란 기록 그래프."""
import math

from PIL import Image, ImageDraw

N = 1024  # 크게 그린 뒤 줄여서 가장자리를 부드럽게


def stroke(d, pts, w, fill):
    """둥근 붓으로 선 긋기 — PIL line은 급한 기울기에서 굵기가 들쭉날쭉해서 점을 촘촘히 찍는다."""
    r = w / 2
    for (ax, ay), (bx, by) in zip(pts, pts[1:]):
        n = max(1, int(math.hypot(bx - ax, by - ay) / 2))
        for k in range(n + 1):
            x, y = ax + (bx - ax) * k / n, ay + (by - ay) * k / n
            d.ellipse([x - r, y - r, x + r, y + r], fill=fill)


def draw() -> Image.Image:
    img = Image.new("RGBA", (N, N), (0, 0, 0, 0))
    # 위→아래로 밝은 파랑에서 화면 강조색(#0071e3)으로 (PSA 뷰어와 같은 바탕)
    grad = Image.new("RGBA", (N, N))
    gd = ImageDraw.Draw(grad)
    for y in range(N):
        t = y / (N - 1)
        gd.line([(0, y), (N, y)], fill=(int(40 * (1 - t)), int(150 + (113 - 150) * t), int(255 + (227 - 255) * t), 255))
    mask = Image.new("L", (N, N), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, N - 1, N - 1], radius=int(N * 0.22), fill=255)
    img.paste(grad, (0, 0), mask)
    d = ImageDraw.Draw(img)

    # 코일: 양쪽 리드선 + 위로 볼록한 반원 3개 (회로도의 인덕터 기호)
    x0, x1, cy = N * 0.12, N * 0.88, N * 0.47
    humps, lead = 3, N * 0.07
    r = (x1 - x0 - 2 * lead) / humps / 2
    pts = [(x0, cy), (x0 + lead, cy)]
    for h in range(humps):
        cx = x0 + lead + r * (2 * h + 1)
        pts += [(cx - r * math.cos(math.pi * k / 60), cy - r * math.sin(math.pi * k / 60)) for k in range(61)]
    pts += [(x1, cy)]
    stroke(d, pts, N * 0.085, "white")

    # 기록 그래프: 측정값 점 4개를 잇는 노란 꺾은선
    gx = [N * 0.22, N * 0.41, N * 0.59, N * 0.78]
    gy = [N * 0.82, N * 0.71, N * 0.76, N * 0.62]
    pts = list(zip(gx, gy))
    stroke(d, pts, N * 0.06, (255, 214, 102, 255))
    for x, y in pts:
        rr = N * 0.065
        d.ellipse([x - rr, y - rr, x + rr, y + rr], fill=(255, 214, 102, 255))
    return img


if __name__ == "__main__":
    big = draw()
    big.resize((256, 256), Image.LANCZOS).save("icon.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
