<script lang="ts">
	import Header from "$lib/Header.svelte"
	import "../style/vars.css"
	import "../style/theme.css"
	import "../style/app.css"
</script>

<Header />

<main>
	<slot />
</main>

<footer>
	<span>open router</span>
</footer>

<style>
	main {
		flex: 1;
		display: flex;
		flex-direction: column;
		padding: 1rem;
		width: 100%;
		max-width: 1024px;
		margin: 0 auto;
		box-sizing: border-box;
	}

	footer {
		display: flex;
		flex-direction: column;
		justify-content: center;
		align-items: center;
		padding: 40px;
	}

	@media (min-width: 480px) {
		footer {
			padding: 40px 0;
		}
	}
</style>
