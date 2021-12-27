import type { EndpointOutput, Request } from '@sveltejs/kit';
import type { Locals } from '$lib/types';
import { getActiveOverview } from "../../services/clients"

export async function api(request: Request<Locals>, resource: string, data?: Record<string, unknown>): Promise<EndpointOutput> {
	// console.log("request", request)
	console.log(`resource: ${resource}`)
	console.log("data", data)

	if (resource) {
		const overview = await getActiveOverview()
		return {
			status: 200,
			body: JSON.stringify(overview)
		}
	}
}
