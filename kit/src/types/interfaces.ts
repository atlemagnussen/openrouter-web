export interface Lease {
    uid: string
    ip: string
    mac: string
    host: string
    start: Date | string
    end: Date | string
}

export interface Range {
    from: string
    to: string
}

export interface LeasesOverView {
    active: Lease[]
    inactive: Lease[]
}