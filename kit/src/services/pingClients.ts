import type { DhcpConfig } from "src/types/interfaces"
import fping from "./fping"

export const getActive = async (conf: DhcpConfig): Promise<string[]> => {
    const net = conf.subnets[0]
    const active = await fping.pingRange(net.subnet, net.netmask)
    return active
}