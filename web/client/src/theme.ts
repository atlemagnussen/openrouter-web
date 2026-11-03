import uPlot from "uplot";
import { bitRate, bitRateShort } from "./format";

export interface Theme {
    series: string[];
    other: string;
    surface: string;
    text: string;
    muted: string;
    grid: string;
}

/** A CSS custom property from site.css, e.g. token("--rtt-1"). */
export function token(name: string): string {
    return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

/** Colours come from the CSS tokens in site.css, so light and dark mode follow the page. */
export function readTheme(): Theme {
    const style = getComputedStyle(document.documentElement);
    const token = (name: string) => style.getPropertyValue(name).trim();
    return {
        series: [1, 2, 3, 4, 5, 6, 7].map(i => token(`--series-${i}`)),
        other: token("--series-other"),
        surface: token("--surface"),
        text: token("--text"),
        muted: token("--muted"),
        grid: token("--border"),
    };
}

export function baseOptions(theme: Theme, width: number, height = 280): Omit<uPlot.Options, "series"> {
    const axis = (values?: uPlot.Axis["values"]): uPlot.Axis => ({
        stroke: theme.muted,
        grid: { stroke: theme.grid, width: 1 },
        ticks: { stroke: theme.grid, width: 1 },
        values,
    });
    return {
        width: Math.max(width, 200),
        height,
        cursor: { points: { size: 8 }, drag: { x: true, y: false } },
        scales: { y: { range: (_u, _min, max) => [0, max > 0 ? max * 1.1 : 1] } },
        axes: [axis(), { ...axis((_u, splits) => splits.map(v => bitRateShort(v))), size: 56 }],
        legend: { live: true },
    };
}

export function line(label: string, color: string, theme: Theme): uPlot.Series {
    return {
        label,
        stroke: color,
        width: 2,
        fill: withAlpha(color, 0.08),
        value: (_u, value) => bitRate(value),
        points: { size: 8, stroke: color, fill: theme.surface },
    };
}

export function timeValue(_u: uPlot, value: number | null): string {
    if (value == null) return "–";
    return new Date(value * 1000).toLocaleString(undefined, {
        weekday: "short", day: "numeric", month: "short", hour: "2-digit", minute: "2-digit",
    });
}

/** "#2a78d6" + 0.85 → "rgba(42, 120, 214, 0.85)". */
export function withAlpha(hex: string, alpha: number): string {
    const value = parseInt(hex.replace("#", ""), 16);
    return `rgba(${(value >> 16) & 255}, ${(value >> 8) & 255}, ${value & 255}, ${alpha})`;
}

