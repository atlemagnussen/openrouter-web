<script context="module" lang="ts">
	import type { Load } from "@sveltejs/kit"
	export const load: Load = async ({ fetch }) => {
		const res = await fetch("/config.json")
		if (res.ok) {
			const config = await res.json()
			return {
				props: { config }
			}
		}
		const { message } = await res.json()
		return {
			error: new Error(message)
		}
	}
</script>

<script lang="ts">
	import type { DhcpConfig } from "../../types/interfaces"
	import HostList from "$lib/HostList.svelte"
	export let config: DhcpConfig = {
		hosts: [],
		subnets: []
	}

</script>

<svelte:head>
	<title>Leases</title>
</svelte:head>
<h1> Config</h1>
<div class="configfile">
	<label for="configfile">Config file:</label>
	<code id="configfile"><i>{config.configFilePath}</i></code>
</div>
<div class="config">
	{#each config.subnets as sub}
		<div class="subnet">
			<p>
				<label for="subnet">Subnet</label>
				<span id="subnet">{sub.subnet}</span>
				<label for="subnet">Netmask</label>
				<span id="subnet">{sub.netmask}</span>
			</p>
			<p>
				<label for="range">Range from</label>
				<span id="range">{sub.range.from}</span>
				<label for="range">to</label>
				<span id="range">{sub.range.to}</span>
			</p>
			<p>
				<label for="routers">Routers</label>
				<span id="routers">{sub.routers.join(" ")}</span>
			</p>
			<p>
				<label for="dns">DNS servers</label>
				<span id="dns">{sub.dns.join(" ")}</span>
			</p>
			<p>
				<label for="leasetime">LeaseTime default</label>
				<span id="routers">{sub.defaultLeaseTime}</span>
				<label for="leasetime">Max</label>
				<span id="routers">{sub.maxLeaseTime}</span>
			</p>
		</div>
		<div class="hosts">
			<HostList list={config.hosts} />
		</div>
	{/each}
</div>

<style>
	.configfile {
		padding: 0.3rem;
		background: var(--secondary-color);
	}
	.config {
		display: flex;
		flex-direction: column;
	}
	
</style>
