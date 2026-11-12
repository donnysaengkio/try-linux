import { useEffect, useRef } from "react";

// The pixel logo, drawn like the Windows edition's: 5×7 letters, a shadow, and a gradient down the rows.
const GLYPHS: Record<string, string[]> = {
  T: ["#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.."],
  R: ["####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#"],
  Y: ["#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#.."],
  L: ["#....", "#....", "#....", "#....", "#....", "#....", "#####"],
  I: ["#####", "..#..", "..#..", "..#..", "..#..", "..#..", "#####"],
  N: ["#...#", "##..#", "##..#", "#.#.#", "#..##", "#..##", "#...#"],
  U: ["#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."],
  X: ["#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#"],
};
const WORD = "TRY LINUX";

export function Logo({ px = 7 }: { px?: number }) {
  const ref = useRef<HTMLCanvasElement>(null);
  const cols = WORD.length * 6 - 1, shadow = Math.max(1, Math.round(px / 3));

  useEffect(() => {
    const paint = () => {
      const g = ref.current?.getContext("2d");
      if (!g) return;
      const css = getComputedStyle(document.documentElement);
      const rgb = (h: string) => [1, 3, 5].map(i => parseInt(h.trim().slice(i, i + 2), 16));
      const mix = (a: string, b: string, t: number) => {
        const [x, y] = [rgb(a), rgb(b)];
        return `rgb(${x.map((v, i) => Math.round(v + (y[i] - v) * t)).join(",")})`;
      };
      const draw = (dx: number, dy: number, color: (row: number) => string) =>
        [...WORD].forEach((ch, i) => (GLYPHS[ch] ?? []).forEach((row, r) => [...row].forEach((cell, c) => {
          if (cell !== "#") return;
          g.fillStyle = color(r);
          g.fillRect(dx + (i * 6 + c) * px, dy + r * px, px, px);
        })));
      g.clearRect(0, 0, g.canvas.width, g.canvas.height);
      draw(shadow, shadow, () => css.getPropertyValue("--accent2"));
      draw(0, 0, r => mix(css.getPropertyValue("--highlight"), css.getPropertyValue("--highlight2"), r / 6));
    };
    paint();
    // Light and dark have their own colours: repaint when Windows or Preferences switches.
    const scheme = matchMedia("(prefers-color-scheme: light)");
    scheme.addEventListener("change", paint);
    addEventListener("themechange", paint);
    return () => { scheme.removeEventListener("change", paint); removeEventListener("themechange", paint); };
  }, [px, shadow]);

  return <canvas ref={ref} className="logo" width={cols * px + shadow} height={7 * px + shadow} aria-label="Try Linux" />;
}
