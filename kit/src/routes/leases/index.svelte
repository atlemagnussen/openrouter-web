<script context="module" lang="ts">
	import type { Load } from "@sveltejs/kit"

	export const load: Load = async ({ fetch }) => {
		const res = await fetch("/leases.json")

		if (res.ok) {
			const leasesOverview = await res.json()

			return {
				props: { leasesOverview }
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
	import type { Lease, LeasesOverview, OverView } from "../../types/interfaces"
	export let leasesOverview: LeasesOverview = {
		configFilePath: "",
		active: [],
		inactive: []
	}

</script>

<svelte:head>
	<title>Leases</title>
</svelte:head>
<h1> Active Leases</h1>
<label for="configfile">Config file</label>
<span id="configfile">{leasesOverview.configFilePath}</span>
<div class="leases">
	{#each leasesOverview.active as act (act.uid)}
		<div class="lease">
			{act.name} - {act.ip} - {act.mac} - 
			<DateTimeViewer date={act.start}></DateTimeViewer>
		</div>
	{/each}
</div>
<h1> Inactive Leases</h1>
<div class="leases">
	{#each leasesOverview.inactive as act}
		<div class="lease">
			{act.name} - {act.ip} - {act.mac} - 
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
