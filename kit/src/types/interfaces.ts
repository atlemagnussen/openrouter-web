export interface IpAddress {
    id?: number
    ip: string
}
export interface Host extends IpAddress {
    name: string
    mac: string
}
export interface Lease extends Host {
    uid?: string
    start?: Date | string
    end?: Date | string
}

export interface Range {
    from: string
    to: string
}

export interface OverView<T extends IpAddress> {
    active: T[]
    inactive: T[]
}

export interface LeasesOverview extends OverView<Lease> {
    configFilePath?: string
}

export interface Network {
    subnet: string
    netmask: string
    routers: string[]
    dns: string[]
    range: Range
    defaultLeaseTime: string
    maxLeaseTime: string
}

export interface DhcpConfig {
    configFilePath?: string
    subnets: Network[]
    hosts: Host[]
}