// The distributions: the built-in five (Recommended, the Windows edition's Distro.Catalog) and the repository's
// catalog, loaded once. Each says where its installer is: `base`, then `steps` added to it (a step starting
// with ~ is a pattern for the newest matching link), and the file with its official checksum.
import { catalog } from "./tauri";

export interface Distro {
  id: string; name: string; home?: string; about?: string;
  base?: string; steps?: string[]; checksum?: string; diskGB?: number;
}

export const RECOMMENDED: Distro[] = [
  { id: "fedora", name: "Fedora Workstation", home: "https://fedoraproject.org/",
    about: "A community distribution sponsored by Red Hat that ships new GNOME, kernel and Mesa releases soon after they come out",
    base: "https://mirrors.kernel.org/fedora/releases/",
    steps: ["~^\\d+/$", "Workstation/x86_64/iso/", "~^Fedora-Workstation-Live-.*\\.iso$"], checksum: "~CHECKSUM$" },
  { id: "ubuntu", name: "Ubuntu Desktop", home: "https://www.ubuntu.com/",
    about: "The most widely used desktop Linux, made by Canonical, with long-term support releases and a large software catalog",
    base: "https://releases.ubuntu.com/",
    steps: ["~^\\d\\d\\.\\d\\d(\\.\\d+)?/$", "~^ubuntu-.*-desktop-amd64\\.iso$"], checksum: "SHA256SUMS" },
  { id: "debian", name: "Debian (GNOME)", home: "https://www.debian.org/",
    about: "One of the oldest community distributions, known for stability and the base of Ubuntu, Mint and many others",
    base: "https://cdimage.debian.org/debian-cd/current-live/amd64/iso-hybrid/",
    steps: ["~^debian-live-.*-amd64-gnome\\.iso$"], checksum: "SHA256SUMS" },
  { id: "mint", name: "Linux Mint (Cinnamon)", home: "https://linuxmint.com/",
    about: "A beginner-friendly distribution based on Ubuntu, with the Windows-like Cinnamon desktop and media codecs available at install",
    base: "https://mirrors.edge.kernel.org/linuxmint/stable/",
    steps: ["~^\\d+(\\.\\d+)?/$", "~^linuxmint-.*-cinnamon-64bit\\.iso$"], checksum: "sha256sum.txt" },
  { id: "zorin", name: "Zorin OS Core", home: "https://zorin.com/os/",
    about: "A polished distribution based on Ubuntu, designed to feel familiar to people moving from Windows",
    base: "https://mirrors.edge.kernel.org/zorinos-isos/",
    steps: ["~^\\d+/$", "~^Zorin-OS-(?<v>\\d+(\\.\\d+)*)-Core-64-bit(-r\\d+)?\\.iso$"], checksum: "SHA256SUMS.txt" },
];

let loading: Promise<Distro[]> | null = null;

// The repository's catalog (empty if it can't be had). Entries whose mirror isn't https lose their download
// (they're shown with their website only), as on the Windows edition.
export function moreDistros(): Promise<Distro[]> {
  loading ??= catalog().then(text => {
    try {
      return ((JSON.parse(text).distros ?? []) as Distro[])
        .filter(d => d.id && d.name && !RECOMMENDED.some(r => r.id === d.id))
        .map(d => d.base?.startsWith("https://") && Array.isArray(d.steps) ? d : { ...d, base: undefined, steps: undefined });
    } catch { return []; }
  }).catch(() => []);
  return loading;
}

export async function findDistro(id: string): Promise<Distro | undefined> {
  if (!id) return undefined;
  return RECOMMENDED.find(d => d.id === id) ?? (await moreDistros()).find(d => d.id === id);
}
