import uPlot from "uplot";
import "uplot/dist/uPlot.min.css";
import "./traffic.css";
import { bitRate } from "./format";
import { LiveView } from "./live";
import { stack, type Column } from "./stack";
import { baseOptions, line, readTheme, timeValue, withAlpha, type Theme } from "./theme";

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
    private readonly tableUrl: string;
    private readonly wanEl: HTMLElement;
    private readonly clientsEl: HTMLElement;
    private readonly errorEl: HTMLElement | null;
    private wan?: uPlot;
    private clients?: uPlot;
    private data?: TrafficSeries;
    private clientKeys = "";

    constructor(private readonly root: HTMLElement) {
        this.url = root.dataset.seriesUrl!;
        this.tableUrl = root.dataset.tableUrl!;
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

    /** Swaps in the server-rendered devices table for the current range. */
    async loadTable(): Promise<void> {
        const container = document.querySelector<HTMLElement>("[data-devices]");
        if (!container) return;
        const response = await fetch(this.tableUrl);
        if (response.ok) container.innerHTML = await response.text();
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

const root = document.getElementById("traffic");
const liveRoot = document.getElementById("traffic-live");
const history = root ? new TrafficView(root) : undefined;
const live = liveRoot && !liveRoot.hidden ? new LiveView(liveRoot) : undefined;
void history?.load();
void live?.load();

// The server says when a new minute is stored ("minute") and sends live rates ("live").
const eventsUrl = liveRoot?.dataset.eventsUrl;
const events = eventsUrl ? new EventSource(eventsUrl) : undefined;
events?.addEventListener("minute", () => {
    void history?.load();
    void history?.loadTable();
});
events?.addEventListener("live", e => live?.add(JSON.parse((e as MessageEvent<string>).data)));
events?.addEventListener("ready", () => live?.setConnected(true));
events?.addEventListener("error", () => live?.setConnected(false)); // EventSource reconnects by itself.

// Fallback while the event stream is down.
setInterval(() => {
    if (events?.readyState === EventSource.OPEN) return;
    void history?.load();
    void history?.loadTable();
}, RefreshMs);

window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => {
    history?.rebuild();
    live?.rebuild();
});
