<script lang="ts">
    import type { Host, Lease } from "src/types/interfaces"
    import DateTimeViewer from "$lib/Components/Formatters/DateTimeViewer.svelte"
    export let list: Lease[] = []
</script>

<table>
    <thead>
        <th>Ip</th>
        <th>Mac</th>
        <th>Name</th>
        <th></th>
    </thead>
    <tbody>
        {#each list as host}
        <tr>
            <td>{host.ip}</td>
            <td>{host.mac}</td>
            <td>{host.name}</td>
            <td>
                {#if host.start}
                    <DateTimeViewer date={host.start} />
                    <DateTimeViewer date={host.end} />
                {/if}
            </td>
        </tr>
        {/each}
    </tbody>
</table>