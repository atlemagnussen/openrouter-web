import Layout from "../components/Layout";
import dataservice from "../services/dataservice";
import PropTypes from "prop-types";

const Config = (props) => {
    return (
        <Layout>
            <h1>Subnets</h1>
            {props.subnets.map(subnet => (
                <p key={subnet.subnet}>
                    subnet: {subnet.subnet} - netmask: {subnet.netmask}<br />
                    default lease time: {subnet.defaultLeaseTime}<br />
                    max lease time: {subnet.maxLeaseTime}<br />
                    dhcp range: {subnet.range.from} - {subnet.range.to}<br />
                    router: {subnet.routers[0]} dns server: {subnet.dns[0]}
                </p>
            ))}
            <h1>Static ips</h1>
            {props.hosts.map(host => (
                <p key={host.ip}>{host.ip} - {host.mac} - {host.name}</p>
            ))}
        </Layout>
    );
};

Config.getInitialProps = async () => {
    const conf = await dataservice.getConfig();
    return conf;
};

Config.propTypes = {
    "subnets": PropTypes.array,
    "hosts": PropTypes.array,
};
export default Config;
