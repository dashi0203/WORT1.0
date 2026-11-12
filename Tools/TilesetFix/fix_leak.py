"""
修补颜色贴图里从参考图漏进来的异色斑块（如砂土贴图左边缘的浅色石灰岩碎块）：
按“低饱和、高亮度”找出漏进来的像素，膨胀一圈后用同一行往右平移 shift 像素处的像素替换（羽化边缘），保持可平铺。
只看贴图四边 band 像素宽的窄带（漏色都在投影的边上），不碰中间正常的浅色小石子。
用法：python3 fix_leak.py 输入.png 输出.png [shift=150] [sat=0.49] [lum=0.5] [band=28]
"""
import sys
import numpy as np
from PIL import Image, ImageFilter

src, dst = sys.argv[1:3]
shift = int(sys.argv[3]) if len(sys.argv) > 3 else 150
sat_t = float(sys.argv[4]) if len(sys.argv) > 4 else 0.49
lum_t = float(sys.argv[5]) if len(sys.argv) > 5 else 0.5
band = int(sys.argv[6]) if len(sys.argv) > 6 else 28
im = np.asarray(Image.open(src).convert('RGB'), np.float64) / 255
mx, mn = im.max(2), im.min(2)
sat = (mx - mn) / np.maximum(mx, 1e-6)
lum = im @ np.array([0.299, 0.587, 0.114])
H, W = lum.shape
yy, xx = np.mgrid[0:H, 0:W]
edge = (xx < band) | (xx >= W - band) | (yy < band) | (yy >= H - band)
mask = ((sat < sat_t) & (lum > lum_t) & edge).astype(np.float64)
m = Image.fromarray((mask * 255).astype(np.uint8)).filter(ImageFilter.MaxFilter(9)).filter(ImageFilter.GaussianBlur(3))
m = np.asarray(m, np.float64)[..., None] / 255
rep = np.roll(im, -shift, axis=1)
out = im * (1 - m) + rep * m
Image.fromarray((np.clip(out, 0, 1) * 255 + 0.5).astype(np.uint8)).save(dst)
print(f'{src.split("/")[-1]}: 修补 {int(mask.sum())} 个像素（羽化后影响 {int((m > 0.05).sum())} 个）')
