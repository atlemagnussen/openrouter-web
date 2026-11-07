/** "850 kbit/s", "12.4 Mbit/s": matches Format.BitRate on the server. */
export function bitRate(bitsPerSecond: number | null | undefined): string {
    if (bitsPerSecond == null) return "–";
    const units = ["bit/s", "kbit/s", "Mbit/s", "Gbit/s"];
    let value = bitsPerSecond;
    let unit = 0;
    while (value >= 1000 && unit < units.length - 1) {
        value /= 1000;
        unit++;
    }
    const digits = unit === 0 || value >= 100 ? 0 : value >= 10 ? 1 : 2;
    return `${value.toFixed(digits)} ${units[unit]}`;
}

/** Short axis label: "12M", "850k". */
export function bitRateShort(bitsPerSecond: number): string {
    const units = ["", "k", "M", "G"];
    let value = bitsPerSecond;
    let unit = 0;
    while (value >= 1000 && unit < units.length - 1) {
        value /= 1000;
        unit++;
    }
    return `${value % 1 === 0 ? value : value.toFixed(1)}${units[unit]}`;
}
