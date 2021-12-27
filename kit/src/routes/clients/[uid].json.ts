import { api } from "./_api"
import type { RequestHandler } from "@sveltejs/kit"
import type { Locals } from "$lib/types"

// GET /leases/:uid.json
export const patch: RequestHandler<Locals, FormData> = async (request) => {
	return api(request, `leases/${request.params.uid}`)
}