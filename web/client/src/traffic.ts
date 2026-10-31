import uPlot from "uplot";
import "uplot/dist/uPlot.min.css";
import "./traffic.css";
import { bitRate, bitRateShort } from "./format";
import { stack, type Column } from "./stack";

/** Matches TrafficSeries in OpenRouter.Web/Traffic (camelCase JSON). Rates are bits per second. */
interface TrafficSeries {
    from: number;
    bucketSeconds: number;
    timestamps: number[];
    wanInterface: string | null;
    wanDown: Column;
    wanUp: Column;
    clients: ClientSeries[];
}

interface ClientSeries {
    key: string;
    name: string;
    network: string | null;
    totalBytes: number;
    bitsPerSecond: Column;
}

const RefreshMs = 60_000;
const Height = 280;

class TrafficView {
    private readonly url: string;
    private readonly wanEl: HTMLElement;
    private readonly clientsEl: HTMLElement;
    private readonly errorEl: HTMLElement | null;
    private wan?: uPlot;
    private clients?: uPlot;
    private data?: TrafficSeries;
    private clientKeys = "";

    constructor(private readonly root: HTMLElement) {
        this.url = root.dataset.seriesUrl!;
        this.wanEl = root.querySelector<HTMLElement>("[data-chart=wan]")!;
        this.clientsEl = root.querySelector<HTMLElement>("[data-chart=clients]")!;
        this.errorEl = root.querySelector<HTMLElement>("[data-chart-error]");
        new ResizeObserver(() => this.resize()).observe(this.clientsEl);
    }

    async load(): Promise<void> {
        try {
            const response = await fetch(this.url, { headers: { Accept: "application/json" } });
            if (!response.ok) throw new Error((await response.json().catch(() => null))?.error ?? response.statusText);
            this.data = (await response.json()) as TrafficSeries;
            this.showError(null);
            this.render();
        } catch (error) {
            this.showError(`Could not load chart data: ${error instanceof Error ? error.message : error}`);
        }
    }

    /** Recreates the charts, e.g. after the colour scheme changed. */
    rebuild(): void {
        this.wan?.destroy();
        this.clients?.destroy();
        this.wan = this.clients = undefined;
        this.render();
    }

    private render(): void {
        const data = this.data;
        if (!data) return;

        const label = this.root.querySelector<HTMLElement>("[data-wan-interface]");
        if (label) label.textContent = data.wanInterface ? `(${data.wanInterface})` : "";

        const wanData: uPlot.AlignedData = [data.timestamps, data.wanDown, data.wanUp];
        if (this.wan) this.wan.setData(wanData);
        else this.wan = new uPlot(this.wanOptions(), wanData, this.wanEl);

        // Series identity (and so colour) follows the device; rebuild when the set of devices changes.
        const keys = data.clients.map(c => c.key).join(",");
        if (this.clients && keys === this.clientKeys) {
            this.restack();
        } else {
            this.clients?.destroy();
            this.clientKeys = keys;
            this.clients = this.createClientChart(data);
        }
    }

    private wanOptions(): uPlot.Options {
        const theme = readTheme();
        return {
            ...baseOptions(theme, this.wanEl.clientWidth),
            series: [
                { label: "Time", value: timeValue },
                line("Download", theme.series[0], theme),
                line("Upload", theme.series[1], theme),
            ],
        };
    }

    private createClientChart(data: TrafficSeries): uPlot {
        const theme = readTheme();
        const raw = data.clients.map(c => c.bitsPerSecond);
        const { data: stacked, bands } = stack(raw, () => false);

        const series: uPlot.Series[] = [{ label: "Time", value: timeValue }];
        data.clients.forEach((client, i) => {
            const color = client.key === "other" ? theme.other : theme.series[i % theme.series.length];
            series.push({
                label: client.name,
                stroke: theme.surface,
                width: 1,
                fill: withAlpha(color, 0.85),
                // The legend shows each device's own rate, not the stacked total.
                value: (u, _value, seriesIndex, index) =>
                    index == null ? "–" : bitRate(rawValue(u, seriesIndex, index)),
                points: { show: false },
            });
        });

        const options: uPlot.Options = {
            ...baseOptions(theme, this.clientsEl.clientWidth),
            series,
            bands,
            hooks: {
                // Re-stack when a device is hidden or shown from the legend.
                setSeries: [() => this.restack()],
            },
        };
        const chart = new uPlot(options, [data.timestamps, ...stacked], this.clientsEl);
        (chart as RawData).raw = raw;
        return chart;
    }

    private restack(): void {
        const chart = this.clients;
        const data = this.data;
        if (!chart || !data) return;
        const raw = data.clients.map(c => c.bitsPerSecond);
        const { data: stacked, bands } = stack(raw, i => !chart.series[i + 1].show);
        (chart as RawData).raw = raw;
        chart.delBand();
        bands.forEach(b => chart.addBand(b));
        chart.setData([data.timestamps, ...stacked]);
    }

    private resize(): void {
        for (const [chart, el] of [[this.wan, this.wanEl], [this.clients, this.clientsEl]] as const)
            chart?.setSize({ width: el.clientWidth, height: Height });
    }

    private showError(message: string | null): void {
        if (!this.errorEl) return;
        this.errorEl.hidden = message == null;
        this.errorEl.textContent = message ?? "";
    }
}

type RawData = uPlot & { raw?: Column[] };

function rawValue(u: uPlot, seriesIndex: number, index: number): number | null {
    return (u as RawData).raw?.[seriesIndex - 1]?.[index] ?? null;
}

interface Theme {
    series: string[];
    other: string;
    surface: string;
    text: string;
    muted: string;
    grid: string;
}

/** Colours come from the CSS tokens in site.css, so light and dark mode follow the page. */
function readTheme(): Theme {
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

function baseOptions(theme: Theme, width: number): Omit<uPlot.Options, "series"> {
    const axis = (values?: uPlot.Axis["values"]): uPlot.Axis => ({
        stroke: theme.muted,
        grid: { stroke: theme.grid, width: 1 },
        ticks: { stroke: theme.grid, width: 1 },
        values,
    });
    return {
        width: Math.max(width, 200),
        height: Height,
        cursor: { points: { size: 8 }, drag: { x: true, y: false } },
        scales: { y: { range: (_u, _min, max) => [0, max > 0 ? max * 1.1 : 1] } },
        axes: [axis(), { ...axis((_u, splits) => splits.map(v => bitRateShort(v))), size: 56 }],
        legend: { live: true },
    };
}

function line(label: string, color: string, theme: Theme): uPlot.Series {
    return {
        label,
        stroke: color,
        width: 2,
        fill: withAlpha(color, 0.08),
        value: (_u, value) => bitRate(value),
        points: { size: 8, stroke: color, fill: theme.surface },
    };
}

function timeValue(_u: uPlot, value: number | null): string {
    if (value == null) return "–";
    return new Date(value * 1000).toLocaleString(undefined, {
        weekday: "short", day: "numeric", month: "short", hour: "2-digit", minute: "2-digit",
    });
}

/** "#2a78d6" + 0.85 → "rgba(42, 120, 214, 0.85)". */
function withAlpha(hex: string, alpha: number): string {
    const value = parseInt(hex.replace("#", ""), 16);
    return `rgba(${(value >> 16) & 255}, ${(value >> 8) & 255}, ${value & 255}, ${alpha})`;
}

const root = document.getElementById("traffic");
if (root) {
    const view = new TrafficView(root);
    void view.load();
    setInterval(() => void view.load(), RefreshMs);
    window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => view.rebuild());
}
