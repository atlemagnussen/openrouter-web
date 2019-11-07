import React from "react";
import Layout from "../components/Layout";
import Link from "next/link";
import combiner from "../services/combiner";
import formatters from "../services/formatters";
import PropTypes from "prop-types";

const Index = props => (
    <Layout>
        <h1>Active Clients</h1>
        {props.clients.active.map(client => (
            <p key={client.id}>
                {client.ip} - 
                {client.mac} - 
                {client.name}
            </p>
        ))}
        <h1>Active Leases</h1>
        {props.leases.active.map(lease => (
            <p key={lease.id}>
                <Link href="/device/[id]" as={`/device/${lease.mac}`}>
                    <a>
                        {lease.ip} - {lease.host} - {formatters.toSmallDateTime(lease.end)}
                    </a>
                </Link>
            </p>
        ))}
        <h1>Inactive Leases</h1>
        {props.leases.inactive.map(lease => (
            <p key={lease.id}>
                <Link href="/device/[id]" as={`/device/${lease.mac}`}>
                    <a>
                        {lease.ip} - {lease.host} - {formatters.toSmallDateTime(lease.end)}
                    </a>
                </Link>
            </p>
        ))}
        <h1>Inactive Clients</h1>
        {props.clients.inactive.map(client => (
            <p key={client.id}>
                {client.ip} - {client.mac} - {client.name}
            </p>
        ))}
    </Layout>
);

Index.getInitialProps = async () => {
    const all = await combiner.getAll();

    return all;
};

Index.propTypes = {
    "clients": PropTypes.array,
    "leases": PropTypes.array,
};

export default Index;
