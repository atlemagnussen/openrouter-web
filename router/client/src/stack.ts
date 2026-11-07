import type uPlot from "uplot";

export type Column = (number | null)[];

/**
 * Stacks series on top of each other (first series at the bottom) and returns the uPlot bands that fill
 * the space between each series and the one below it. Hidden series are left out of the stack.
 * After uPlot's stacking demo.
 */
export function stack(raw: Column[], hidden: (index: number) => boolean): { data: Column[]; bands: uPlot.Band[] } {
    const length = raw[0]?.length ?? 0;
    const sum = new Array<number>(length).fill(0);
    const data = raw.map((column, i) =>
        hidden(i) ? column : column.map((value, j) => (value == null ? null : (sum[j] += value))),
    );

    // uPlot series indexes start at 1 (0 is the x axis).
    const bands: uPlot.Band[] = [];
    let below = -1;
    raw.forEach((_, i) => {
        if (hidden(i)) return;
        if (below >= 0) bands.push({ series: [i + 1, below + 1] });
        below = i;
    });
    return { data, bands };
}
