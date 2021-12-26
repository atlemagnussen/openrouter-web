<script lang="ts">
	import { formatterNoDateAndTime } from "./DateTime"
    export let date: Date | string
</script>
<style>
	
</style>

{#if !date}
	<span>N/A</span>
{:else}
	<span>{formatterNoDateAndTime(date)}</span>
{/if}