<script context="module" lang="ts">
	import type { Load } from "@sveltejs/kit"
	export const load: Load = async ({ fetch }) => {
		const res = await fetch("/clients.json")
		if (res.ok) {
			console.log("got clients")
			const overview = await res.json()
			return {
				props: { overview }
			}
		}
		const { message } = await res.json()
		return {
			error: new Error(message)
		}
	}
</script>

<script lang="ts">
	import type { Host, OverView } from "../../types/interfaces"
	import HostList from "$lib/HostList.svelte"
	export let overview: OverView<Host> = {
		active: [],
		inactive: []
	}
</script>

<svelte:head>
	<title>Active clients</title>
</svelte:head>
<h1>Clients</h1>
<div class="config">
	<HostList list={overview.active} />
</div>

<style>
	.config {
		display: flex;
		flex-direction: column;
	}
	
</style>
