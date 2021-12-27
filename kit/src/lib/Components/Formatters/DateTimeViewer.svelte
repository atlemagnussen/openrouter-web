<script lang="ts">
	import { formatterNoDateAndTimeHardCode } from "./DateTime"
    export let date: Date | string

</script>
<style>
	
</style>

{#if !date}
	<span>N/A</span>
{:else}
	<span>{formatterNoDateAndTimeHardCode(date)}</span>
{/if}