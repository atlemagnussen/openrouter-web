import { api } from "./_api"
import type { RequestHandler } from "@sveltejs/kit"
import type { Locals } from "$lib/types"

// GET /todos.json
export const get: RequestHandler<Locals> = async (request) => {
	// request.locals.userid comes from src/hooks.js
	const response = await api(request, "leases")

	if (response.status === 404) {
		// user hasn't created a todo list.
		// start with an empty array
		return { body: [] }
	}

	return response;
}