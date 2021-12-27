<script lang="ts">
	import { formatterNoDateAndTimeHardCode } from "./DateTime"
    export let date: Date | string

</script>
<style>
	
</style>

{#if !date}
	<small>N/A</small>
{:else}
	<small>{formatterNoDateAndTimeHardCode(date)}</small>
{/if}