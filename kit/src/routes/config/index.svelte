<script context="module" lang="ts">
	import type { Load } from "@sveltejs/kit"

	export const load: Load = async ({ fetch }) => {
		const res = await fetch("/config.json")

		if (res.ok) {
			const config = await res.json()

			return {
				props: { config }
			};
		}

		const { message } = await res.json()

		return {
			error: new Error(message)
		}
	}
</script>

<script lang="ts">
	import DateTimeViewer from "$lib/Components/Formatters/DateTimeViewer.svelte"
	import type { DhcpConfig, LeasesOverView } from "../../types/interfaces"
	export let config: DhcpConfig = {
		hosts: [],
		subnets: []
	}

</script>

<svelte:head>
	<title>Leases</title>
</svelte:head>
<h1> Active Leases</h1>
<div class="leases">
	{#each config.subnets as sub}
		<div class="subnet">
			<p>
				<label for="subnet">Subnet</label>
				<span id="subnet">{sub.subnet}</span>
			</p>
			<DateTimeViewer date={act.start}></DateTimeViewer>
		</div>
	{/each}
</div>
<h1> Inactive Leases</h1>
<div class="leases">
	{#each leasesOverview.inactive as act}
		<div class="lease">
			{act.host} - {act.ip} - {act.mac} - 
			<DateTimeViewer date={act.start}></DateTimeViewer>
		</div>
	{/each}
</div>

<style>
	.leases {
		display: flex;
		flex-direction: column;
	}
	.lease {
		width: 100%;
		line-height: 1;
	}
</style>
