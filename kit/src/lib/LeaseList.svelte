<script lang="ts">
    import type { Lease } from "src/types/interfaces"
    import DateTimeViewer from "$lib/Components/Formatters/DateTimeViewer.svelte"
    export let list: Lease[] = []
</script>

<table>
    <thead>
        <th>Ip</th>
        <th>Mac</th>
        <th>Name</th>
        <th>start</th>
        <th>end</th>
    </thead>
    <tbody>
        {#each list as host}
        <tr>
            <td>{host.ip}</td>
            <td>{host.mac}</td>
            <td>{host.name}</td>
            <td>
                <DateTimeViewer date={host.start} />
            </td>
            <td>
                <DateTimeViewer date={host.end} />
            </td>
        </tr>
        {/each}
    </tbody>
</table>