import uPlot from "uplot";
import { bitRate } from "./format";
import { baseOptions, line, readTheme, timeValue } from "./theme";

/** Matches LivePoint in OpenRouter.Web/Traffic (camelCase JSON). Rates are bits per second. */
export interface LivePoint {
    time: number;
    wanDown: number;
    wanUp: number;
    clients: LiveClient[];
}

interface LiveClient {
    key: string;
    name: string;
    network: string | null;
    down: number;
    up: number;
}

const WindowSeconds = 10 * 60;
const Height = 200;
const RowsShown = 6;

/** The moving WAN chart and the "busiest right now" table at the top of the Traffic page. */
export class LiveView {
    private readonly url: string;
    private readonly chartEl: HTMLElement;
    private readonly nowEl: HTMLElement | null;
    private readonly stateEl: HTMLElement | null;
    private points: LivePoint[] = [];
    private chart?: uPlot;

    constructor(root: HTMLElement) {
        this.url = root.dataset.liveUrl!;
        this.chartEl = root.querySelector<HTMLElement>("[data-chart=live]")!;
        this.nowEl = root.querySelector<HTMLElement>("[data-live-now]");
        this.stateEl = root.querySelector<HTMLElement>("[data-live-state]");
        new ResizeObserver(() => this.chart?.setSize({ width: this.chartEl.clientWidth, height: Height })).observe(this.chartEl);
    }

    /** The last few minutes the server kept, so the chart starts full. */
    async load(): Promise<void> {
        const response = await fetch(this.url, { headers: { Accept: "application/json" } });
        if (!response.ok) return;
        const recent = (await response.json()) as LivePoint[];
        // Keep anything that arrived over the event stream while this was loading.
        const newest = recent.at(-1)?.time ?? 0;
        this.points = [...recent, ...this.points.filter(p => p.time > newest)];
        this.render();
    }

    add(point: LivePoint): void {
        if (this.points.length > 0 && point.time <= this.points.at(-1)!.time) return;
        this.points.push(point);
        const oldest = point.time - WindowSeconds;
        while (this.points.length > 0 && this.points[0].time < oldest) this.points.shift();
        this.render();
    }

    setConnected(connected: boolean): void {
        if (!this.stateEl) return;
        this.stateEl.textContent = connected ? "live" : "reconnecting…";
        this.stateEl.classList.toggle("connected", connected);
    }

    rebuild(): void {
        this.chart?.destroy();
        this.chart = undefined;
        this.render();
    }

    private render(): void {
        const data: uPlot.AlignedData = [
            this.points.map(p => p.time),
            this.points.map(p => p.wanDown),
            this.points.map(p => p.wanUp),
        ];
        if (this.chart) {
            this.chart.setData(data);
        } else {
            const theme = readTheme();
            this.chart = new uPlot({
                ...baseOptions(theme, this.chartEl.clientWidth, Height),
                series: [{ label: "Time", value: timeValue }, line("Download", theme.series[0], theme), line("Upload", theme.series[1], theme)],
            }, data, this.chartEl);
        }
        this.renderNow();
    }

    /** The newest point's busiest devices. Built with DOM nodes, so device names are never treated as HTML. */
    private renderNow(): void {
        const latest = this.points.at(-1);
        if (!this.nowEl || !latest) return;
        const rows = latest.clients.filter(c => c.down + c.up > 0).slice(0, RowsShown);
        if (rows.length === 0) {
            this.nowEl.replaceChildren(row([cell("No device traffic right now", "muted", 3)]));
            return;
        }
        this.nowEl.replaceChildren(...rows.map(c =>
            row([cell(c.name), cell(bitRate(c.down), "num"), cell(bitRate(c.up), "num")])));
    }
}

function row(cells: HTMLTableCellElement[]): HTMLTableRowElement {
    const tr = document.createElement("tr");
    tr.append(...cells);
    return tr;
}

function cell(text: string, className?: string, colSpan?: number): HTMLTableCellElement {
    const td = document.createElement("td");
    td.textContent = text;
    if (className) td.className = className;
    if (colSpan) td.colSpan = colSpan;
    return td;
}
